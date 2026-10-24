using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupraChat.Automation;
using SupraChat.Core;

namespace SupraChat.PeerLedgerRdte;

[SupportedOSPlatform("windows")]
internal sealed class SharedDesktopHost : IDisposable
{
    internal sealed record Request(string SessionId, string Key, long Version = 0, string? Value = null);
    internal sealed record Envelope(string? Operation, string? Method, string? Jsonrpc, int? Id, Request Params);
    internal sealed record Reply(string Status, long Version, long Cursor, int TraceEvents);
    internal sealed record Projection(int Session, string Title, long Version, long Cursor, int TraceEvents);
    private readonly WindowsOwnerCustody _custody = WindowsOwnerCustody.Create();
    private readonly W12ScopedGrantAuthority _issuer;
    private W9DurableSessionStore _store;
    private SharedSessionLedger _ledger;
    private readonly TraceNormalizer _trace = new();
    private readonly string[] _sessions;
    private readonly LocalTraceSession[] _traceCaps;
    private readonly string[] _ownerGrants;
    private readonly string[] _peerGrants;
    private readonly CancellationTokenSource _closing = new(TimeSpan.FromSeconds(90));
    private readonly ConcurrentBag<Task> _workers = new();
    private static readonly JsonSerializerOptions WireOptions = new(TraceNormalizer.JsonOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private int _peers, _clientRuns, _revocationRuns;
    public int GuiMutations { get; private set; }
    public bool ClientsComplete { get; private set; }
    public bool AgentRevoked { get; private set; }

    public SharedDesktopHost()
    {
        using var material = _custody.InitializeIssuer();
        _issuer = material.CreateAuthority();
        _store = OpenStore(create: true);
        _ledger = new(_store.Load(), _store.Save);
        _sessions = new[] { "Session A", "Session B" }.Select((title, i) =>
            _ledger.Open("shared-lab-open-session-" + i, "automation", title).Session!.Id).ToArray();
        _traceCaps = _sessions.Select(id => _trace.CreateSession(Guid.ParseExact(id, "N"))).ToArray();
        _ownerGrants = _sessions.Select(id => _issuer.Issue("local-owner-fixture", "automation", id,
            ["session.read", "session.mutate"], TimeSpan.FromMinutes(2))).ToArray();
        _peerGrants = _sessions.Select((id, i) => _issuer.Issue(i == 0 ? "automation-fixture" : "agent-fixture",
            i == 0 ? "automation" : "agent", id, ["session.read", "session.mutate"], TimeSpan.FromMinutes(2))).ToArray();
        foreach (var cap in _traceCaps)
        {
            _trace.SetCapture(cap, TraceCaptureMode.Metadata);
            _trace.TryObserve(cap, new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Local, TraceEventKind.Request));
        }
    }

    private W9DurableSessionStore OpenStore(bool create)
    {
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY", "yes");
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE", create ? "yes" : null);
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS", null);
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_FAULT", null);
        Environment.SetEnvironmentVariable("SUPRACHAT_W14_WINDOWS_CUSTODY", "synthetic");
        return CustodyNative.WithUserFileOwner(() => new W9DurableSessionStore(Path.Combine(_custody.Root, "fixture.sqlite")));
    }

    private SessionLedgerAdmission OwnerAdmission(int index, string action)
    {
        var grant = _issuer.Validate(_ownerGrants[index], _sessions[index], action, "local-owner-fixture", "automation");
        return new(grant.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId);
    }

    public Projection Read(int index)
    {
        var view = _ledger.InspectProjectionAdmitted(_sessions[index], () => OwnerAdmission(index, "session.read"), session =>
            new Projection(index, session.Title, session.Version, _ledger.InspectAll().Sequence, TraceCount(index, session.Id)));
        return view.Projection ?? throw new UnauthorizedAccessException("LAB_READ_REFUSED");
    }

    public string Rename(int index, long version, string title)
    {
        var result = _ledger.ExecuteAdmitted(new("set-title", _sessions[index], "gui-" + Guid.NewGuid().ToString("N"), version, title),
            () => OwnerAdmission(index, "session.mutate"));
        if (result.Applied) GuiMutations++;
        return result.Status;
    }

    private int TraceCount(int index, string sessionId)
    {
        var page = _trace.Read(_traceCaps[index]);
        if (page.SessionId.ToString("N") != sessionId || page.Events.Any(e => e.SessionId.ToString("N") != sessionId))
            throw new UnauthorizedAccessException("LAB_TRACE_SESSION_REFUSED");
        return page.Events.Count;
    }

    public async Task RunClientsAsync()
    {
        if (Interlocked.Exchange(ref _clientRuns, 1) != 0) throw new InvalidOperationException("LAB_CLIENTS_ALREADY_RUN");
        var a = Task.Run(() => ServeAsync(0, revoke: false));
        var b = Task.Run(() => ServeAsync(1, revoke: false));
        _workers.Add(a); _workers.Add(b);
        await Task.WhenAll(a, b);
        ClientsComplete = true;
    }

    public async Task RevokeAgentAsync()
    {
        if (!ClientsComplete || Interlocked.Exchange(ref _revocationRuns, 1) != 0)
            throw new InvalidOperationException("LAB_REVOCATION_UNAVAILABLE");
        var worker = Task.Run(() => ServeAsync(1, revoke: true));
        _workers.Add(worker); await worker;
        AgentRevoked = true;
    }

    private async Task ServeAsync(int index, bool revoke)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var pipeName = "suprachat-shared-lab-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        var audience = index == 0 ? "automation" : "agent";
        var actor = index == 0 ? "automation-fixture" : "agent-fixture";
        using var child = Program.Spawn("--shared-client", pipeName, audience, revoke ? "revoke" : "mutate");
        try
        {
            var pid = child.Id; var started = child.StartTime.ToUniversalTime();
            await pipe.WaitForConnectionAsync(timeout.Token);
            var hello = await Program.ReadFrameAsync(pipe, timeout.Token);
            if (!hello.AsSpan().SequenceEqual("suprachat-shared-lab/v1"u8) || !Program.ObservePeer(pipe, pid, started))
                throw new UnauthorizedAccessException("LAB_PEER_REFUSED");
            // A non-authority welcome supplies synthetic IDs/revision; no grant or principal is accepted from the child.
            var initial = Read(index);
            await Program.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new
            { session_id = _sessions[index], other_session_id = _sessions[1 - index], version = initial.Version }), timeout.Token);
            var methods = revoke ? new[] { "session/revoke-self", "session/inspect" } :
                new[] { "session/inspect", "session/change", "trace/read", "session/inspect" };
            for (var n = 0; n < methods.Length; n++)
            {
                var bytes = await Program.ReadFrameAsync(pipe, timeout.Token);
                var envelope = JsonSerializer.Deserialize<Envelope>(bytes, WireOptions) ?? throw new InvalidDataException();
                var format = audience == "agent"
                    ? envelope.Jsonrpc == "2.0" && envelope.Operation is null && envelope.Method == methods[n] && envelope.Id == n + 1
                    : envelope.Jsonrpc is null && envelope.Method is null && envelope.Id is null && envelope.Operation == methods[n];
                if (!format || !Program.ObservePeer(pipe, pid, started)) throw new UnauthorizedAccessException("LAB_PEER_REFUSED");
                var request = envelope.Params;
                var operation = methods[n] switch { "session/change" => "set-title", "session/revoke-self" => "revoke-self", _ => "inspect" };
                SessionLedgerAdmission Admit()
                {
                    if (!Program.ObservePeer(pipe, pid, started)) return new(false, null, null, null, null);
                    var grant = _issuer.Validate(_peerGrants[index], request.SessionId,
                        operation == "inspect" ? "session.read" : "session.mutate", actor, audience);
                    return new(grant.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId);
                }
                Reply reply;
                if (methods[n] == "trace/read")
                {
                    var projection = _ledger.InspectProjectionAdmitted(request.SessionId, Admit,
                        session => new Reply("OBSERVED", session.Version, _ledger.InspectAll().Sequence, TraceCount(index, session.Id)));
                    reply = projection.Projection ?? new(projection.Admission.Status, -1, -1, 0);
                }
                else
                {
                    var result = _ledger.ExecuteAdmitted(new(operation, request.SessionId, request.Key, request.Version, request.Value), Admit);
                    reply = new(result.Status, result.Session?.Version ?? -1, result.Sequence, 0);
                }
                var expected = revoke ? (n == 0 ? "REVOKED" : "GRANT_REVOKED") :
                    n == 1 ? "APPLIED" : n == 3 ? "ADMISSION_REFUSED" : "OBSERVED";
                if (reply.Status != expected) throw new InvalidOperationException("LAB_PROTOCOL_OUTCOME_REFUSED");
                var response = audience == "agent"
                    ? JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = envelope.Id, result = reply }, WireOptions)
                    : JsonSerializer.SerializeToUtf8Bytes(reply, WireOptions);
                await Program.WriteFrameAsync(pipe, response, timeout.Token);
            }
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("LAB_CLIENT_FAILED");
            Interlocked.Increment(ref _peers);
        }
        finally { await Program.CleanupAsync(child); }
    }

    internal static async Task<int> ClientAsync(string name, string audience, string mode)
    {
        if (audience is not ("automation" or "agent") || mode is not ("mutate" or "revoke")) return 64;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        await Program.WriteFrameAsync(pipe, "suprachat-shared-lab/v1"u8.ToArray(), timeout.Token);
        using var welcome = JsonDocument.Parse(await Program.ReadFrameAsync(pipe, timeout.Token));
        var session = welcome.RootElement.GetProperty("session_id").GetString()!;
        var other = welcome.RootElement.GetProperty("other_session_id").GetString()!;
        var version = welcome.RootElement.GetProperty("version").GetInt64();
        var methods = mode == "revoke" ? new[] { "session/revoke-self", "session/inspect" } :
            new[] { "session/inspect", "session/change", "trace/read", "session/inspect" };
        for (var n = 0; n < methods.Length; n++)
        {
            var request = new Request(mode == "mutate" && n == 3 ? other : session, audience + "-shared-mutation-key", version, audience + "-updated");
            var envelope = new Envelope(audience == "automation" ? methods[n] : null,
                audience == "agent" ? methods[n] : null, audience == "agent" ? "2.0" : null, audience == "agent" ? n + 1 : null, request);
            await Program.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope, TraceNormalizer.JsonOptions), timeout.Token);
            using var response = JsonDocument.Parse(await Program.ReadFrameAsync(pipe, timeout.Token));
            var result = response.RootElement;
            if (audience == "agent")
            {
                if (result.GetProperty("jsonrpc").GetString() != "2.0" || result.GetProperty("id").GetInt32() != n + 1 ||
                    result.TryGetProperty("error", out _)) return 72;
                result = result.GetProperty("result");
            }
            var reply = result.Deserialize<Reply>(WireOptions)!;
            var expected = mode == "revoke" ? (n == 0 ? "REVOKED" : "GRANT_REVOKED") :
                n == 1 ? "APPLIED" : n == 3 ? "ADMISSION_REFUSED" : "OBSERVED";
            if (reply.Status != expected || mode == "mutate" && n is 1 or 2 && reply.Version != version + 1) return 72;
        }
        return 0;
    }

    public object VerifyAndRecover()
    {
        if (!ClientsComplete || !AgentRevoked || GuiMutations < 1 || _peers != 3) throw new InvalidOperationException("LAB_INCOMPLETE");
        var before = new[] { Read(0), Read(1) };
        _store.Dispose(); _store = OpenStore(create: false); _ledger = new(_store.Load(), _store.Save);
        var after = new[] { Read(0), Read(1) };
        var grant = _issuer.Validate(_peerGrants[1], _sessions[1], "session.read");
        var copied = false;
        var recoveredRead = _ledger.InspectProjectionAdmitted(_sessions[1], () =>
            new SessionLedgerAdmission(grant.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId),
            session => { copied = true; return session.Version; });
        if (!grant.Allowed || !_ledger.IsGrantRevoked(grant.GrantId!) || !before.SequenceEqual(after) ||
            recoveredRead.Admission.Status != "GRANT_REVOKED" || copied ||
            after[0].Version < 2 || after[1].Version != 1 || after.Any(p => p.TraceEvents != 1))
            throw new InvalidOperationException("LAB_RECOVERY_REFUSED");
        return new
        {
            shared_sessions = 2, gui_mutations = GuiMutations, enrolled_peer_runs = _peers,
            automation_and_agent_protocols = true, wrong_session_refused = true, revoked_read_refused = true,
            versions = after.Select(p => p.Version).ToArray(), common_cursor = after.Select(p => p.Cursor).Distinct().Count() == 1,
            durable_revocation_recovered = true, metadata_recovered = true, trace_session_routing = true
        };
    }

    public void Dispose()
    {
        _closing.Cancel();
        try { if (!Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(5))) Environment.Exit(78); }
        catch (AggregateException) { /* Failed workers are terminal; their finally blocks drained their owned children. */ }
        _store.Dispose(); _issuer.Dispose(); _trace.Dispose(); _custody.Dispose(); _closing.Dispose();
    }
}
