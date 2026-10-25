using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SupraChat.Automation;
using SupraChat.Core;

namespace SupraChat.PeerLedgerRdte;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string Sentinel = "synthetic-peer-ledger-private-sentinel";
    private sealed record Request(string Operation, string SessionId, string Key, long Version, string? Value, string Audience);
    private sealed record CaseReceipt(string Case, bool Passed, bool PeerObserved, string Status);
    private sealed record Policy(string Actor, string Audience, string Session, string[] Scopes);
    private static readonly List<WindowsOwnerCustody> CustodyRoots = new();
    private static bool _custodyMode;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 64;
        if (args.Length == 2 && args[0] == "--control-receipt") return await DurableControlStoreChecks.RunAsync(args[1]);
        if (args.Length == 3 && args[0] == "--control-crash") return DurableControlStoreChecks.Child(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--metadata-consent-receipt") return SharedDesktopLab.Run(args[1], requireConsent: true);
        if (args.Length == 2 && args[0] == "--shared-desktop-receipt") return SharedDesktopLab.Run(args[1]);
        if (args.Length == 4 && args[0] == "--shared-client")
        {
            try { return await SharedDesktopHost.ClientAsync(args[1], args[2], args[3]); } catch { return 71; }
        }
        if (args.Length == 5 && args[0] == "--peer")
        {
            try { await ChildAsync(args[1], args[2], args[3], args[4]); return 0; } catch { return 71; }
        }
        if (args.Length == 3 && args[0] is "--crash" or "--custody-crash")
        {
            _custodyMode = args[0] == "--custody-crash";
            return CrashWriter(args[1], args[2]);
        }
        if (args.Length == 2 && args[0] == "--custody-reopen") return CustodyChecks.Reopen(args[1]);
        if (args.Length != 2 || args[0] is not ("--receipt" or "--custody-receipt")) return 64;
        _custodyMode = args[0] == "--custody-receipt";
        var cases = new List<CaseReceipt>();
        var failure = "NONE";
        IReadOnlyList<CustodyChecks.Check> custodyChecks = Array.Empty<CustodyChecks.Check>();
        try
        {
            await QualifyAsync(cases);
            if (_custodyMode) custodyChecks = await CustodyChecks.RunAsync(Spawn);
        }
        catch (Exception ex)
        {
            failure = ex.Message switch
            {
                "CUSTODY_CHECK_FAILED" => "CHECK_FAILED",
                "CUSTODY_REFUSAL_NOT_OBSERVED" => "REFUSAL_NOT_OBSERVED",
                "CUSTODY_OWNER_ACL_REFUSED" => "OWNER_ACL_REFUSED",
                "CUSTODY_ACCESS_RULE_REFUSED" => "ACCESS_RULE_REFUSED",
                "CUSTODY_FINAL_PATH_REFUSED" => "FINAL_PATH_REFUSED",
                "RESTRICTED_TOKEN_UNAVAILABLE" => "RESTRICTED_TOKEN_UNAVAILABLE",
                "RESTRICTED_TOKEN_NOT_RESTRICTED" => "RESTRICTED_TOKEN_NOT_RESTRICTED",
                _ => "INCOMPLETE"
            };
            cases.Add(new("host-boundary", false, false, "INCOMPLETE"));
        }
        finally { foreach (var root in CustodyRoots) root.Dispose(); }
        var passed = cases.Count == 17 && cases.All(c => c.Passed) &&
            (!_custodyMode || custodyChecks.Count == 15 && custodyChecks.All(c => c.Passed));
        var receipt = JsonSerializer.Serialize(new
        {
            schema = _custodyMode ? "suprachat-windows-custody-rdte/v1" : "suprachat-peer-ledger-integration/v1", passed,
            scope = "SYNTHETIC_ENROLLED_CHILD_METADATA_ONLY", cases,
            windows_custody_mode = _custodyMode, custody_checks = custodyChecks,
            custody_phase = CustodyChecks.Phase, failure,
            os_peer_observed = true, one_w6_mutation_gate = true, one_w9_checkpoint_authority = true,
            grants_exported = false, signing_key_exported = false, peer_identity_values_exported = false,
            human_identity_qualified = false, production_issuer_custody_qualified = false,
            different_user_denial_qualified = false, general_path_race_resistance_qualified = false,
            owner_admin_resistance_qualified = false,
            key_protection = _custodyMode ? "WINDOWS_DPAPI_CURRENT_USER_SYNTHETIC" : "EPHEMERAL_PARENT",
            shared_provider_control_qualified = false, first_party_capture = false,
            trace_completeness = "UNKNOWN", durable_trace_recovery = false
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });
        using var identity = WindowsIdentity.GetCurrent();
        if (receipt.Contains(Sentinel) || receipt.Contains(identity.User!.Value) || receipt.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return 72;
        var path = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(output)) writer.Write(receipt);
        Console.WriteLine(passed ? "Peer-ledger metadata integration PASS; production admission remains unqualified." : "Peer-ledger integration incomplete.");
        return passed ? 0 : 1;
    }

    private static void StoreAdmission(bool create, string? fault = null)
    {
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY", "yes");
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE", create ? "yes" : null);
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS", fault is null ? null : "1");
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_FAULT", fault);
        Environment.SetEnvironmentVariable("SUPRACHAT_W14_WINDOWS_CUSTODY", _custodyMode ? "synthetic" : null);
    }

    private static string FreshStore()
    {
        if (_custodyMode)
        {
            var custody = WindowsOwnerCustody.Create();
            CustodyRoots.Add(custody);
            return Path.Combine(custody.Root, "fixture.sqlite");
        }
        var directory = Path.Combine(Path.GetTempPath(), "suprachat-w9-owned-peer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "fixture.sqlite"); // Retained disposable evidence; never delete an inferred path.
    }

    private static async Task QualifyAsync(List<CaseReceipt> cases)
    {
        StoreAdmission(create: true);
        var path = FreshStore();
        string a, b, revokedGrant;
        using var trace = new TraceNormalizer();
        var now = DateTimeOffset.UtcNow;
        using var persistedIssuer = _custodyMode ? CustodyRoots[0].InitializeIssuer() : null;
        var key = _custodyMode ? null : RandomNumberGenerator.GetBytes(32);
        using var issuer = persistedIssuer?.CreateAuthority(() => now) ?? new W12ScopedGrantAuthority(key!, () => now);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        using (var store = OpenStore(path))
        {
            var ledger = new SharedSessionLedger(store.Load(), store.Save);
            // Trusted fixture setup, never a remotely exposed or claimed-human operation.
            a = ledger.Open("fixture-open-session-a", "automation", "fixture-a").Session!.Id;
            b = ledger.Open("fixture-open-session-b", "automation", "fixture-b").Session!.Id;
            var ca = trace.CreateSession(Guid.ParseExact(a, "N"));
            var cb = trace.CreateSession(Guid.ParseExact(b, "N"));
            trace.SetCapture(ca, TraceCaptureMode.Metadata);
            trace.SetCapture(cb, TraceCaptureMode.Metadata);
            trace.TryObserve(ca, new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Local, TraceEventKind.Request, StreamId: Sentinel));
            trace.TryObserve(cb, new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Local, TraceEventKind.Response, StreamId: Sentinel));
            var caps = new Dictionary<string, LocalTraceSession> { [a] = ca, [b] = cb };
            var automation = new Policy("fixture-automation", "automation", a, ["session.mutate", "session.read"]);
            var agent = new Policy("fixture-agent", "agent", b, ["session.mutate", "session.read"]);
            async Task Run(string name, Policy policy, string expected, string? token = null) =>
                cases.Add(await RunCaseAsync(name, policy, a, b, issuer, ledger, trace, caps, expected, token));
            await Run("mutate", automation, "APPLIED");
            await Run("retry", automation, "REPLAY");
            await Run("conflict", automation, "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT");
            await Run("stale", automation, "VERSION_CONFLICT");
            await Run("wrong-session", automation, "ADMISSION_REFUSED");
            await Run("forged-human", automation, "PEER_POLICY_REFUSED");
            await Run("read-only-write", automation with { Scopes = ["session.read"] }, "ADMISSION_REFUSED");
            await Run("unenrolled", automation, "PEER_POLICY_REFUSED");
            await Run("agent-mutate", agent, "APPLIED");
            await Run("trace-read", automation, "TRACE_OBSERVED");
            await Run("wrong-trace", automation, "ADMISSION_REFUSED");
            var token = issuer.Issue(automation.Actor, automation.Audience, a, automation.Scopes, TimeSpan.FromMinutes(2));
            revokedGrant = issuer.Validate(token, a, "session.mutate").GrantId!;
            await Run("revoke-self", automation, "REVOKED", token);
            await Run("revoked-retry", automation, "GRANT_REVOKED", token);
            var expired = issuer.Issue(automation.Actor, automation.Audience, a, automation.Scopes, TimeSpan.FromMinutes(2));
            now = now.AddMinutes(3);
            await Run("expired-grant", automation, "ADMISSION_REFUSED", expired);
            Require(ledger.Inspect(a)!.Version == 1 && ledger.Inspect(b)!.Version == 1);
            Require(trace.Read(ca).Events.All(e => e.SessionId.ToString("N") == a) && trace.Read(cb).Events.All(e => e.SessionId.ToString("N") == b));
        }
        StoreAdmission(create: false);
        using (var recovered = OpenStore(path))
        {
            var ledger = new SharedSessionLedger(recovered.Load(), recovered.Save);
            Require(ledger.IsGrantRevoked(revokedGrant) && ledger.Inspect(a)!.Version == 1 && ledger.Inspect(b)!.Version == 1);
            var replay = ledger.ExecuteAdmitted(new("set-title", a, "shared-operation-key", 0, "one"), () =>
                new(true, "fixture-automation", "automation", a, Guid.NewGuid().ToString("N")));
            Require(replay.IsReplay && replay.Session!.Version == 1);
            // Trace observations do not enter authoritative checkpoints or imply restart completeness.
            using var newTrace = new TraceNormalizer();
            var restored = newTrace.CreateSession(Guid.ParseExact(a, "N"));
            Require(newTrace.Read(restored).Events.Count == 0 && newTrace.Read(restored).Completeness == "UNKNOWN");
        }
        // Independent subprocesses exercise actual W9 process termination, then restart/replay.
        foreach (var fault in new[] { "BEFORE_COMMIT", "AFTER_COMMIT_BEFORE_ACK", "WRITE_FAILURE" })
            cases.Add(await CheckCrashAsync(fault));
    }

    private static async Task<CaseReceipt> RunCaseAsync(string scenario, Policy policy, string a, string b,
        W12ScopedGrantAuthority issuer, SharedSessionLedger ledger, TraceNormalizer trace,
        Dictionary<string, LocalTraceSession> capabilities, string expected, string? suppliedToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var pipeName = "suprachat-peer-ledger-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        var token = suppliedToken ?? issuer.Issue(policy.Actor, policy.Audience, policy.Session, policy.Scopes, TimeSpan.FromMinutes(2));
        using var child = Spawn("--peer", pipeName, scenario, a, b);
        try
        {
            var enrolledPid = scenario == "unenrolled" ? Environment.ProcessId : child.Id;
            var enrolledStart = child.StartTime.ToUniversalTime();
            await pipe.WaitForConnectionAsync(deadline.Token);
            var request = JsonSerializer.Deserialize<Request>(await ReadFrameAsync(pipe, deadline.Token)) ?? throw new InvalidDataException();
            var observed = ObservePeer(pipe, enrolledPid, enrolledStart);
            var status = "PEER_POLICY_REFUSED";
            if (observed && request.Audience == policy.Audience &&
                request.Operation is "set-title" or "inspect" or "revoke-self" or "trace-read")
            {
                var op = request.Operation == "trace-read" ? "inspect" : request.Operation;
                SessionLedgerAdmission Admit()
                {
                    // Admission is repeated INSIDE the W6 gate; no actor-supplied principal or global bearer.
                    if (!ObservePeer(pipe, enrolledPid, enrolledStart)) return new(false, null, null, null, null);
                    var action = op == "inspect" ? "session.read" : "session.mutate";
                    var grant = issuer.Validate(token, request.SessionId, action, policy.Actor, request.Audience);
                    return new(grant.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId);
                }
                if (request.Operation == "trace-read")
                {
                    var read = ledger.InspectProjectionAdmitted(request.SessionId, Admit,
                        session => trace.Read(capabilities[session.Id], limit: 256));
                    status = read.Admission.Status;
                    if (read.Projection is { } page)
                    {
                        Require(page.Events.Count == 1 && page.Events.All(e => e.SessionId.ToString("N") == read.Admission.Session!.Id));
                        Require(!JsonSerializer.Serialize(page, TraceNormalizer.JsonOptions).Contains(Sentinel));
                        status = "TRACE_OBSERVED";
                    }
                }
                else
                {
                    var result = ledger.ExecuteAdmitted(new(op, request.SessionId, request.Key, request.Version, request.Value), Admit);
                    status = result.IsReplay ? "REPLAY" : result.Status;
                }
            }
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { status }), deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            return new(scenario, (observed || scenario == "unenrolled") && child.ExitCode == 0 && status == expected, observed, status);
        }
        finally { await CleanupAsync(child); }
    }

    internal static bool ObservePeer(NamedPipeServerStream pipe, int expectedPid, DateTime expectedStart)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != expectedPid) return false;
        using var peer = Process.GetProcessById(checked((int)pid));
        if (peer.StartTime.ToUniversalTime() != expectedStart) return false;
        string? sid = null;
        var identified = false;
        pipe.RunAsClient(() =>
        {
            using var token = WindowsIdentity.GetCurrent();
            sid = token.User?.Value;
            identified = token.ImpersonationLevel is TokenImpersonationLevel.Identification or TokenImpersonationLevel.Impersonation;
        });
        using var owner = WindowsIdentity.GetCurrent();
        return identified && sid is not null && sid == owner.User?.Value;
    }

    private static async Task ChildAsync(string pipeName, string scenario, string a, string b)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        var session = scenario is "wrong-session" or "wrong-trace" or "agent-mutate" ? b : a;
        var operation = scenario is "trace-read" or "wrong-trace" ? "trace-read" : scenario == "revoke-self" ? "revoke-self" : "set-title";
        var key = scenario is "mutate" or "retry" or "conflict" or "revoked-retry" or "agent-mutate" ? "shared-operation-key" : "fixture-operation-" + scenario;
        var request = new Request(operation, session, key, 0, scenario == "conflict" ? "changed" : "one",
            scenario == "forged-human" ? "human" : scenario == "agent-mutate" ? "agent" : "automation");
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request), timeout.Token);
        await ReadFrameAsync(pipe, timeout.Token);
    }

    internal static Process Spawn(params string[] arguments)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../.."));
        var start = new ProcessStartInfo(Path.Combine(root, "dotnet.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException();
    }

    internal static async Task CleanupAsync(Process child)
    {
        if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static int CrashWriter(string path, string fault)
    {
        if (fault is not ("BEFORE_COMMIT" or "AFTER_COMMIT_BEFORE_ACK" or "WRITE_FAILURE")) return 64;
        StoreAdmission(create: false, fault);
        using var store = OpenStore(path);
        var ledger = new SharedSessionLedger(store.Load(), store.Save);
        var session = ledger.InspectAll().Sessions.Single();
        ledger.Change("crash-operation-key", "automation", session.Id, 0, "set-title", "after");
        return 73; // The durability hook must terminate the child before this line.
    }

    private static async Task<CaseReceipt> CheckCrashAsync(string fault)
    {
        var path = FreshStore();
        StoreAdmission(create: true);
        using (var store = OpenStore(path))
            new SharedSessionLedger(store.Load(), store.Save).Open("crash-open-session", "automation", "before");
        using var child = Spawn(_custodyMode ? "--custody-crash" : "--crash", path, fault);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await child.WaitForExitAsync(timeout.Token);
            Require(child.ExitCode == (fault == "BEFORE_COMMIT" ? 88 : fault == "AFTER_COMMIT_BEFORE_ACK" ? 89 : 78));
        }
        finally { await CleanupAsync(child); }
        StoreAdmission(create: false);
        using var recovered = OpenStore(path);
        var ledger = new SharedSessionLedger(recovered.Load(), recovered.Save);
        var session = ledger.InspectAll().Sessions.Single();
        Require(session.Version == (fault == "AFTER_COMMIT_BEFORE_ACK" ? 1 : 0));
        var replay = ledger.Change("crash-operation-key", "automation", session.Id, 0, "set-title", "after");
        Require(replay.Applied && replay.IsReplay == (fault == "AFTER_COMMIT_BEFORE_ACK") && ledger.Inspect(session.Id)!.Version == 1);
        return new("checkpoint-" + fault.ToLowerInvariant(), true, false, "RESTART_REPLAY_VERIFIED");
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, token);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 2048) throw new InvalidDataException();
        var data = new byte[length];
        await pipe.ReadExactlyAsync(data, token);
        return data;
    }
    internal static async Task WriteFrameAsync(Stream pipe, byte[] data, CancellationToken token)
    {
        if (data.Length is < 1 or > 2048) throw new InvalidDataException();
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(data, token);
        await pipe.FlushAsync(token);
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException(); }
    private static W9DurableSessionStore OpenStore(string path) => _custodyMode
        ? CustodyNative.WithUserFileOwner(() => new W9DurableSessionStore(path)) : new W9DurableSessionStore(path);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
