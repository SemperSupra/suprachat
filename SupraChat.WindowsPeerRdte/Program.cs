using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SupraChat.WindowsPeerRdte;

internal static class Program
{
    private const string Sentinel = "synthetic-peer-private-sentinel";
    private sealed record PeerCase(string Case, bool OsProcessBound, bool OsSubjectMatchesOwner, bool OsImpersonationObserved, bool ClaimedPidMatches,
        bool Admitted, string Reason);

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 64;
        if (args.Length == 3 && args[0] == "--child")
        {
            try { await ChildAsync(args[1], args[2]); return 0; } catch { return 71; }
        }
        if (args.Length != 2 || args[0] != "--receipt") return 64;
        var output = Path.GetFullPath(args[1]);
        if (File.Exists(output)) return 64;
        var observations = new List<PeerCase>();
        var passed = false;
        try
        {
            foreach (var scenario in new[] { "automation", "agent", "forged-role", "forged-pid", "unenrolled-peer", "claimed-subject", "oversized-frame" })
                observations.Add(await RunCaseAsync(scenario));
            passed = observations.Count == 7 && observations.All(c =>
                c.OsSubjectMatchesOwner && c.OsImpersonationObserved && (c.Case == "unenrolled-peer" ? !c.OsProcessBound : c.OsProcessBound) &&
                c.Admitted == (c.Case is "automation" or "agent"));
        }
        catch { observations.Add(new("host-boundary", false, false, false, false, false, "PROBE_INCOMPLETE")); }
        var receipt = JsonSerializer.Serialize(new
        {
            schema = "suprachat-windows-peer-binding-rdte/v1", passed,
            evidence = "LOCAL_WINDOWS_OS_PEER_OBSERVED", scope = "DISPOSABLE_PARENT_CHILD_PIPE_FIXTURE",
            current_user_only_pipe = true, os_subject_values_exported = false, process_ids_exported = false,
            grant_issuer_production_qualified = false, human_identity_qualified = false,
            different_user_denial_qualified = false, remote_admission_qualified = false,
            provider_actions = false, credentials_used = false, observations
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var current = WindowsIdentity.GetCurrent();
        var sid = current.User?.Value ?? throw new InvalidOperationException("Owner subject unavailable.");
        if (receipt.Contains(sid, StringComparison.Ordinal) || receipt.Contains(Sentinel, StringComparison.Ordinal) ||
            receipt.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return 72;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, receipt);
        Console.WriteLine(passed ? "Windows OS peer-binding fixture PASS; production identity remains unqualified." : "Windows peer-binding fixture incomplete.");
        return passed ? 0 : 1;
    }

    private static async Task<PeerCase> RunCaseAsync(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "suprachat-peer-rdte-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../.."));
        var start = new ProcessStartInfo(Path.Combine(root, "dotnet.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add(name); // A fresh rendezvous name, never a credential or grant.
        start.ArgumentList.Add(scenario);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Fixture child unavailable.");
        try
        {
            var enrolledPid = scenario == "unenrolled-peer" ? Environment.ProcessId : child.Id;
            var enrolledStart = scenario == "unenrolled-peer" ? Process.GetCurrentProcess().StartTime.ToUniversalTime() : child.StartTime.ToUniversalTime();
            await pipe.WaitForConnectionAsync(deadline.Token);
            var frame = await ReadFrameAsync(pipe, deadline.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var observedPid)) throw new InvalidOperationException("Peer PID unavailable.");
            using var observedProcess = Process.GetProcessById(checked((int)observedPid));
            var processBound = observedPid == enrolledPid && observedProcess.StartTime.ToUniversalTime() == enrolledStart;
            string? peerSid = null;
            var impersonationObserved = false;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(); peerSid = identity.User?.Value;
                impersonationObserved = identity.ImpersonationLevel is TokenImpersonationLevel.Identification or TokenImpersonationLevel.Impersonation;
            });
            using var owner = WindowsIdentity.GetCurrent();
            var subjectMatches = peerSid is not null && peerSid == owner.User?.Value;
            var claimedPidMatches = false;
            var reason = "FRAME_BOUNDS_REFUSED";
            var admitted = false;
            if (frame is not null)
            {
                using var doc = JsonDocument.Parse(frame);
                var request = doc.RootElement;
                claimedPidMatches = request.TryGetProperty("claimed_pid", out var claim) && claim.TryGetUInt32(out var claimedPid) && claimedPid == observedPid;
                var allowedFields = request.EnumerateObject().All(p => p.Name is "claimed_pid" or "role");
                var expectedRole = scenario == "agent" ? "agent" : "automation";
                var roleMatches = request.TryGetProperty("role", out var role) && role.GetString() == expectedRole;
                reason = !subjectMatches ? "OS_SUBJECT_REFUSED" : !processBound ? "UNENROLLED_OS_PROCESS" :
                    !allowedFields ? "CLAIMED_AUTHORITY_REFUSED" : !claimedPidMatches ? "CLAIMED_PID_REFUSED" :
                    !roleMatches ? "ROLE_ESCALATION_REFUSED" : "ENROLLED_FIXTURE_PEER";
                admitted = subjectMatches && processBound && allowedFields && claimedPidMatches && roleMatches;
            }
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { admitted, reason }), deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("Fixture child failed.");
            return new(scenario, processBound, subjectMatches, impersonationObserved, claimedPidMatches, admitted, reason);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }

    private static async Task ChildAsync(string name, string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token);
        byte[] frame = scenario switch
        {
            "claimed-subject" => JsonSerializer.SerializeToUtf8Bytes(new { claimed_pid = Environment.ProcessId, role = "automation", actor_subject = Sentinel }),
            "oversized-frame" => new byte[1025],
            _ => JsonSerializer.SerializeToUtf8Bytes(new
            {
                claimed_pid = scenario == "forged-pid" ? 0 : Environment.ProcessId,
                role = scenario == "forged-role" ? "human" : scenario == "agent" ? "agent" : "automation"
            })
        };
        if (scenario == "oversized-frame")
        {
            var header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, 1025);
            await pipe.WriteAsync(header, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
        }
        else await WriteFrameAsync(pipe, frame, deadline.Token);
        if (await ReadFrameAsync(pipe, deadline.Token) is null) throw new InvalidOperationException("Fixture response unavailable.");
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 1024) return null;
        var frame = new byte[size];
        await stream.ReadExactlyAsync(frame, cancellationToken);
        return frame;
    }
    private static async Task WriteFrameAsync(Stream stream, byte[] frame, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
