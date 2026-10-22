using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SupraChat.Core;

/// <summary>
/// W3 provisional lifecycle: a separate, explicitly launched, stdin-closed-to-
/// callers PowerShell child per selected session. No PTY, no commands sent, no
/// textual output retained, no ChatGPT provider connection. This is NOT a
/// kernel sandbox: RSS/time/output are observed with bounded polling and
/// terminate-on-overage; they are not hard pre-allocation limits.
/// </summary>
public sealed class WorkbenchTerminalSupervisor : IAsyncDisposable
{
    public const string Schema = "suprachat-terminal-supervisor/v1";
    public const int MaximumLiveProcesses = 2;
    public const int MaximumOutputBytes = 32 * 1024;
    public const long MaximumObservedRssBytes = 256L * 1024 * 1024;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(90);

    private readonly SessionWorkbench _workbench;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public static object DescribeCapabilities() => new
    {
        schema = Schema,
        current_phase = "W3_SUPERVISED_IDLE_PROCESS_ONLY",
        max_owned_processes = MaximumLiveProcesses,
        max_runtime_seconds = (int)MaximumLifetime.TotalSeconds,
        max_observed_rss_bytes = MaximumObservedRssBytes,
        max_discarded_output_bytes = MaximumOutputBytes,
        explicit_human_start_only = true,
        source_binary_provenance_attested = false,
        process_kernel_sandboxed = false,
        rss_hard_limit = false,
        cpu_quota_enforced = false,
        command_input_available = false,
        pty_available = false,
        browser_connected = false,
        chat_submission_available = false,
        automated_start_available = false,
        terminal_output_retained = false
    };

    public WorkbenchTerminalSupervisor(SessionWorkbench workbench) =>
        _workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));

    /// <summary>
    /// This must only be called following an explicit human UI start action.
    /// Requires a selected session, no collision, and an absolute pwsh path.
    /// Arguments and stdin are fixed. No arbitrary command string is accepted.
    /// </summary>
    public WorkbenchTerminalStatus Start(string sessionId, string absolutePwshPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("SESSION_ID_REQUIRED");
        if (string.IsNullOrWhiteSpace(absolutePwshPath) ||
            !Path.IsPathFullyQualified(absolutePwshPath) ||
            !File.Exists(absolutePwshPath) ||
            !IsPowerShellExecutable(absolutePwshPath))
            throw new ArgumentException("EXPLICIT_INSTALLED_PWSH_PATH_REQUIRED");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = _workbench.Snapshot();
            if (state.ActiveSessionId != sessionId)
                throw new InvalidOperationException("TERMINAL_SESSION_NOT_SELECTED");
            if (state.Collisions.Any(c => c.LeftSessionId == sessionId || c.RightSessionId == sessionId))
                throw new InvalidOperationException("TERMINAL_SESSION_COLLISION_REFUSED");
            if (_entries.TryGetValue(sessionId, out var existing) && !existing.Ended)
                throw new InvalidOperationException("TERMINAL_ALREADY_RUNNING");
            if (_entries.Values.Count(x => !x.Ended) >= MaximumLiveProcesses)
                throw new InvalidOperationException("TERMINAL_CONCURRENCY_LIMIT");

            var config = new ProcessStartInfo(absolutePwshPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var arg in new[] {"-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-"})
                config.ArgumentList.Add(arg);

            // Restrict ambient credential propagation. This is environment
            // hygiene, NOT security isolation or OS-enforced containment.
            config.Environment.Clear();
            foreach (var name in new[] {
                "PATH", "Path", "SystemRoot", "WINDIR", "HOME", "USERPROFILE",
                "TEMP", "TMP", "TMPDIR", "LANG", "DOTNET_ROOT"
            }) {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) config.Environment[name] = value;
            }
            config.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
            config.Environment["SUPRACHAT_TERMINAL_NO_INPUT"] = "1";

            var process = new Process {StartInfo = config, EnableRaisingEvents = true};
            try
            {
                if (!process.Start()) throw new InvalidOperationException("TERMINAL_START_DECLINED");
            }
            catch
            {
                process.Dispose();
                throw;
            }
            // Deliberately leave redirected stdin pipe open but inaccessible;
            // this keeps the shell idle. No code path ever writes to it.
            var entry = new Entry(sessionId, process);
            _entries[sessionId] = entry;
            entry.Watcher = Task.Run(() => WatchAsync(entry));
            return Status(entry);
        }
    }

    public WorkbenchTerminalStatus? Inspect(string sessionId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(sessionId, out var entry) ? Status(entry) : null;
        }
    }

    public async Task<WorkbenchTerminalStatus?> StopAsync(string sessionId)
    {
        Entry? entry;
        lock (_gate) _entries.TryGetValue(sessionId, out entry);
        if (entry is null) return null;
        Terminate(entry, "OPERATOR_STOP");
        if (entry.Watcher is not null) await entry.Watcher.ConfigureAwait(false);
        // The first watcher pass may have failed to verify termination.
        // Keep the OS handle and retry once under a bounded wait; never
        // report STOPPED just because a Kill() call was made.
        if (!entry.Ended)
        {
            Terminate(entry, "STOP_RETRY");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await entry.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            catch (ObjectDisposedException) { }
            VerifyAndRelease(entry);
        }
        return Status(entry);
    }

    public async Task ForgetSessionAsync(string sessionId)
    {
        var result = await StopAsync(sessionId).ConfigureAwait(false);
        if (result is {Running: true})
            throw new InvalidOperationException("CHILD_TERMINATION_NOT_VERIFIED");
        lock (_gate) _entries.Remove(sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
        }
        foreach (var entry in entries) Terminate(entry, "APPLICATION_SHUTDOWN");
        await Task.WhenAll(entries.Select(e => e.Watcher ?? Task.CompletedTask)).ConfigureAwait(false);
        foreach (var entry in entries) VerifyAndRelease(entry);
        lock (_gate)
        {
            if (_entries.Values.Any(e => !e.Ended))
                throw new InvalidOperationException("CHILD_TERMINATION_NOT_VERIFIED");
            _entries.Clear();
        }
    }

    private static bool IsPowerShellExecutable(string path) =>
        string.Equals(Path.GetFileName(path),
            OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh", StringComparison.OrdinalIgnoreCase);

    private static WorkbenchTerminalStatus Status(Entry e) =>
        new(Schema, e.SessionId, e.ProcessId, !e.Ended,
            e.Reason ?? (e.Ended ? "EXITED" : "SUPERVISED_IDLE_NO_INPUT"),
            Interlocked.Read(ref e.OutputBytes), MaximumOutputBytes,
            MaximumObservedRssBytes, (int)MaximumLifetime.TotalSeconds,
            e.StartedAt, e.EndedAt,
            CommandInputAllowed: false, TerminalCommandsExecutedBySupraChat: false,
            ChatSubmissionAllowed: false, PtyAvailable: false, KernelSandboxed: false);

    private static void Terminate(Entry e, string reason)
    {
        if (e.Ended) return;
        Interlocked.CompareExchange(ref e.Reason, reason, null);
        try
        {
            if (!e.Process.HasExited) e.Process.Kill(entireProcessTree: true);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception)
        {
            Interlocked.CompareExchange(ref e.Reason, "TERMINATION_FAILED", reason);
        }
    }

    private static async Task PumpAsync(Stream input, Entry e)
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var length = await input.ReadAsync(buffer).ConfigureAwait(false);
                if (length == 0) return;
                var count = Interlocked.Add(ref e.OutputBytes, length);
                if (count > MaximumOutputBytes)
                {
                    Terminate(e, "OUTPUT_LIMIT");
                    return;
                }
            }
        }
        catch (IOException) { /* process termination may close the pipe */ }
        catch (ObjectDisposedException) { /* expected during shutdown */ }
    }

    private static async Task WatchAsync(Entry e)
    {
        var stdout = PumpAsync(e.Process.StandardOutput.BaseStream, e);
        var stderr = PumpAsync(e.Process.StandardError.BaseStream, e);
        try
        {
            while (true)
            {
                if (e.Process.HasExited) break;
                if (DateTimeOffset.UtcNow - e.StartedAt > MaximumLifetime)
                {
                    Terminate(e, "LIFETIME_LIMIT");
                    break;
                }
                try
                {
                    e.Process.Refresh();
                    if (e.Process.WorkingSet64 > MaximumObservedRssBytes)
                    {
                        Terminate(e, "OBSERVED_RSS_LIMIT");
                        break;
                    }
                }
                catch (InvalidOperationException) { break; }
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (!e.Process.HasExited)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await e.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { Terminate(e, "TERMINATION_TIMEOUT"); }
            }
        }
        catch (Exception) { Terminate(e, "SUPERVISOR_FAILURE"); }
        finally
        {
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { Terminate(e, "PIPE_DRAIN_TIMEOUT"); }
            VerifyAndRelease(e);
        }
    }

    private static void VerifyAndRelease(Entry entry)
    {
        if (entry.Ended) return;
        bool exited;
        try { exited = entry.Process.HasExited; }
        catch (ObjectDisposedException)
        {
            // An already-disposed handle is not evidence of a verified exit.
            entry.Reason = "TERMINATION_UNVERIFIED";
            return;
        }
        if (!exited)
        {
            entry.Reason = "TERMINATION_UNVERIFIED";
            return;
        }

        entry.EndedAt = DateTimeOffset.UtcNow;
        entry.Ended = true;
        entry.Reason ??= "PROCESS_EXITED";
        try { entry.Process.StandardInput.Dispose(); } catch { }
        entry.Process.Dispose();
    }

    private sealed class Entry
    {
        public Entry(string sessionId, Process process)
        {
            SessionId = sessionId;
            Process = process;
            ProcessId = process.Id;
        }
        public string SessionId { get; }
        public Process Process { get; }
        public int ProcessId { get; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt;
        public volatile bool Ended;
        public string? Reason;
        public long OutputBytes;
        public Task? Watcher;
    }
}

public sealed record WorkbenchTerminalStatus(
    string Schema, string SessionId, int ProcessId, bool Running,
    string State, long DiscardedOutputBytes, int MaxOutputBytes,
    long MaxObservedRssBytes, int MaxLifetimeSeconds,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    bool CommandInputAllowed, bool TerminalCommandsExecutedBySupraChat,
    bool ChatSubmissionAllowed, bool PtyAvailable, bool KernelSandboxed);
