using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SupraChat.Core;

/// <summary>
/// Manages a paired local terminal runner bound to a session workbench.
/// Executes commands locally, captures stdout/stderr, and bridges cleanly
/// with TerminalTransferBridge for selective draft staging.
/// </summary>
public sealed class SessionTerminalHost : IDisposable
{
    private readonly object _lock = new();
    private Process? _activeProcess;
    private CancellationTokenSource? _activeCts;
    private readonly StringBuilder _consoleBuffer = new();
    private readonly StringBuilder _lastCommandOutput = new();
    private const int MaxBufferLength = 256 * 1024; // 256 KiB console scrollback limit

    public event Action<string>? OutputReceived;
    public event Action<bool>? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _activeProcess is not null && !_activeProcess.HasExited;
            }
        }
    }

    public string? LastCommand { get; private set; }
    public string LastOutput
    {
        get
        {
            lock (_lock)
            {
                return _lastCommandOutput.ToString();
            }
        }
    }

    public string FullScrollback
    {
        get
        {
            lock (_lock)
            {
                return _consoleBuffer.ToString();
            }
        }
    }

    public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

    public async Task<int> ExecuteCommandAsync(string command, CancellationToken externalCt = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command cannot be empty", nameof(command));

        CancellationTokenSource linkedCts;
        Process process;

        lock (_lock)
        {
            if (_activeProcess is not null && !_activeProcess.HasExited)
                throw new InvalidOperationException("A command is already executing in this paired terminal.");

            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            linkedCts = _activeCts;

            LastCommand = command;
            _lastCommandOutput.Clear();
            AppendToBuffer($"\n> {command}\n");

            var isWindows = OperatingSystem.IsWindows();
            var shell = isWindows ? "powershell.exe" : "bash";
            var args = isWindows
                ? $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{EscapeForCmd(command)}\""
                : $"-c \"{command.Replace("\"", "\\\"")}\"";

            var resolvedDir = Directory.Exists(WorkingDirectory) ? WorkingDirectory : Directory.GetCurrentDirectory();

            var psi = new ProcessStartInfo(shell, args)
            {
                WorkingDirectory = resolvedDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _activeProcess = process;
        }

        StateChanged?.Invoke(true);

        try
        {
            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_lock)
                    {
                        AppendToBuffer(e.Data + Environment.NewLine);
                        _lastCommandOutput.AppendLine(e.Data);
                    }
                    OutputReceived?.Invoke(e.Data);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_lock)
                    {
                        AppendToBuffer("[stderr] " + e.Data + Environment.NewLine);
                        _lastCommandOutput.AppendLine("[stderr] " + e.Data);
                    }
                    OutputReceived?.Invoke("[stderr] " + e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = linkedCts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { /* Ignore race on exit */ }
            });

            await process.WaitForExitAsync(linkedCts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            AppendToBuffer("\n[Command cancelled by operator]\n");
            return -1;
        }
        catch (Exception ex)
        {
            AppendToBuffer($"\n[Terminal execution error: {ex.Message}]\n");
            return -1;
        }
        finally
        {
            lock (_lock)
            {
                _activeProcess = null;
            }
            StateChanged?.Invoke(false);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                _activeCts?.Cancel();
                if (_activeProcess is not null && !_activeProcess.HasExited)
                {
                    _activeProcess.Kill(entireProcessTree: true);
                }
            }
            catch { /* Ignore */ }
        }
    }

    public void ClearScrollback()
    {
        lock (_lock)
        {
            _consoleBuffer.Clear();
            _lastCommandOutput.Clear();
        }
        OutputReceived?.Invoke("\x1Bc"); // ANSI terminal clear signal
    }

    private void AppendToBuffer(string text)
    {
        _consoleBuffer.Append(text);
        if (_consoleBuffer.Length > MaxBufferLength)
        {
            _consoleBuffer.Remove(0, _consoleBuffer.Length - MaxBufferLength);
        }
    }

    private static string EscapeForCmd(string command)
    {
        return command.Replace("\"", "`\"");
    }

    public void Dispose()
    {
        Stop();
        _activeCts?.Dispose();
        _activeProcess?.Dispose();
    }
}
