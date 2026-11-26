using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SupraChat.Core;

namespace SupraChat;

public partial class InteractiveTerminalView : UserControl
{
    private const int BufferLimit = 256 * 1024;
    private static readonly Lazy<string> TerminalDocument = new(() =>
        ReadAsset("terminal.html")
            .Replace("/* XTERM_CSS */", ReadAsset("xterm.css"), StringComparison.Ordinal)
            .Replace("/* XTERM_JS */", ReadAsset("xterm.js"), StringComparison.Ordinal)
            .Replace("/* FIT_JS */", ReadAsset("addon-fit.js"), StringComparison.Ordinal));
    private readonly object _outputGate = new();
    private readonly StringBuilder _history = new();
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _renderTimer;
    private WindowsInteractiveTerminal? _shell;
    private string _documentToken = string.Empty;
    private bool _ready;
    private bool _rendering;
    private bool _resizing;
    private bool _starting;
    private bool _closing;
    private bool _selectionRequested;
    private int _columns = 80;
    private int _rows = 24;

    internal Func<(string Directory, IReadOnlyDictionary<string, string>? Environment)>? GetSettings { get; set; }
    internal event Action<string>? SelectionForChat;

    public InteractiveTerminalView()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _renderTimer.Tick += (_, _) => _ = RenderOutputAsync();
        TerminalView.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            if (args is Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.UserDataFolder = Path.Combine(AppState.DirectoryPath, "terminal-webview");
                windows.ProfileName = "SupraChatTerminal";
                windows.IsInPrivateModeEnabled = true;
            }
        };
        TerminalView.NavigationStarted += (_, args) =>
        {
            if (args.Request != new Uri("about:blank"))
                args.Cancel = true;
        };
        TerminalView.NewWindowRequested += (_, args) => args.Handled = true;
        TerminalView.AdapterCreated += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closing)
                    LoadTerminalDocument();
            });
        TerminalView.AdapterDestroyed += (_, _) =>
        {
            _documentToken = string.Empty;
            _ready = false;
            _selectionRequested = false;
            StartShell.IsEnabled = false;
            _renderTimer.Stop();
        };
        TerminalView.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess && !_closing && !_ready)
                InteractiveStatus.Text = "Terminal view failed to load. Select Retry terminal view.";
        };
        TerminalView.WebMessageReceived += (_, args) => ReceiveMessage(args.Body);
    }

    private async void LoadTerminalDocument()
    {
        _ready = false;
        _selectionRequested = false;
        StartShell.IsEnabled = false;
        _renderTimer.Stop();
        InteractiveStatus.Text = "Loading interactive terminal…";
        var token = Guid.NewGuid().ToString("N");
        _documentToken = token;
        TerminalView.NavigateToString(TerminalDocument.Value.Replace("TERMINAL_DOCUMENT_TOKEN", token, StringComparison.Ordinal));
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (!_closing && !_ready && _documentToken == token)
            InteractiveStatus.Text = "Terminal view did not connect. Select Retry terminal view.";
    }

    private void RetryView_Click(object? sender, RoutedEventArgs e) => LoadTerminalDocument();

    internal void CloseShell()
    {
        _closing = true;
        _renderTimer.Stop();
        _shell?.Stop();
    }

    internal void FocusInput()
    {
        TerminalView.Focus();
        _ = SendToViewAsync(_documentToken, "focus");
    }

    private void ReceiveMessage(string? message)
    {
        if (_closing || string.IsNullOrEmpty(_documentToken) || string.IsNullOrEmpty(message) || message.Length > 110_000)
            return;
        try
        {
            using var document = JsonDocument.Parse(message);
            var data = document.RootElement;
            if (data.GetProperty("token").GetString() != _documentToken)
                return;
            switch (data.GetProperty("kind").GetString())
            {
                case "ready" when !_ready:
                    ReadSize(data);
                    lock (_outputGate)
                    {
                        _pending.Clear();
                        _pending.Append(_history);
                    }
                    _ready = true;
                    _renderTimer.Start();
                    StartShell.IsEnabled = !_starting && _shell is null;
                    InteractiveStatus.Text = _shell is null ? "Choose a shell, then select Start shell." : "Shell running.";
                    break;
                case "input" when _ready && _shell is not null:
                    if (data.GetProperty("text").GetString() is { Length: > 0 and <= 16384 } input &&
                        !_shell.Send(input))
                        InteractiveStatus.Text = "Terminal input queue is full or the shell has stopped.";
                    break;
                case "resize" when _ready:
                    ReadSize(data);
                    break;
                case "selection" when _selectionRequested:
                    _selectionRequested = false;
                    if (data.GetProperty("text").GetString() is { Length: > 0 } selection &&
                        Encoding.UTF8.GetByteCount(selection) <= 16 * 1024)
                        SelectionForChat?.Invoke(selection);
                    else
                        InteractiveStatus.Text = "Select up to 16 KiB of terminal text to review.";
                    break;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Ignore incomplete messages from a terminal page that is being detached.
        }
    }

    private void ReadSize(JsonElement data)
    {
        _columns = data.GetProperty("cols").GetInt32();
        _rows = data.GetProperty("rows").GetInt32();
        if (_shell is { } shell && !_resizing)
            _ = ResizeShellAsync(shell);
    }

    private async Task ResizeShellAsync(WindowsInteractiveTerminal shell)
    {
        _resizing = true;
        try
        {
            (int Columns, int Rows) size;
            do
            {
                size = (_columns, _rows);
                await Task.Run(() => shell.Resize(size.Columns, size.Rows));
            } while (ReferenceEquals(_shell, shell) && size != (_columns, _rows));
        }
        finally
        {
            _resizing = false;
        }
    }

    private async void StartShell_Click(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _closing || _starting || _shell is not null || GetSettings is null)
            return;
        _starting = true;
        StartShell.IsEnabled = false;
        ShellChoice.IsEnabled = false;
        try
        {
            var (directory, variables) = GetSettings();
            var (executable, arguments) = SelectedShell();
            lock (_outputGate)
            {
                _history.Clear();
                _pending.Clear();
            }
            await SendToViewAsync(_documentToken, "reset");
            var (columns, rows) = (_columns, _rows);
            var shell = await Task.Run(() => new WindowsInteractiveTerminal(executable, arguments,
                directory, variables, columns, rows, AppendOutput));
            _starting = false;
            if (_closing)
                shell.Stop();
            else
            {
                _shell = shell;
                if ((columns, rows) != (_columns, _rows) && !_resizing)
                    _ = ResizeShellAsync(shell);
                StopShell.IsEnabled = true;
                InteractiveStatus.Text = "Shell running. Type here to use cmd, PowerShell or a CLI on PATH.";
                FocusInput();
            }
            var exitCode = await shell.Completion;
            if (!_closing)
                InteractiveStatus.Text = $"Shell exited ({exitCode}).";
        }
        catch (Exception error) when (error is Win32Exception or System.Runtime.InteropServices.COMException
            or IOException or InvalidOperationException or ArgumentException)
        {
            if (!_closing)
                InteractiveStatus.Text = $"Interactive shell unavailable. {error.Message}";
        }
        finally
        {
            _starting = false;
            _shell = null;
            if (!_closing)
            {
                StartShell.IsEnabled = _ready;
                ShellChoice.IsEnabled = true;
                StopShell.IsEnabled = false;
            }
        }
    }

    private (string Executable, string Arguments) SelectedShell() => ShellChoice.SelectedIndex switch
    {
        1 => (FindPowerShell(), "-NoLogo"),
        2 => (Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/D"),
        _ => (Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo")
    };

    private static string FindPowerShell()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7"));
        foreach (var directory in directories)
        {
            var absolute = directory.Trim().Trim('"');
            if (Path.IsPathFullyQualified(absolute) && File.Exists(Path.Combine(absolute, "pwsh.exe")))
                return Path.Combine(absolute, "pwsh.exe");
        }
        throw new InvalidOperationException("PowerShell 7 (pwsh) was not found on PATH.");
    }

    private void StopShell_Click(object? sender, RoutedEventArgs e)
    {
        StopShell.IsEnabled = false;
        InteractiveStatus.Text = "Stopping shell…";
        _shell?.Stop();
    }

    private async void StageSelection_Click(object? sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        _selectionRequested = true;
        if (!await SendToViewAsync(_documentToken, "selection"))
            _selectionRequested = false;
    }

    private void AppendOutput(string text)
    {
        lock (_outputGate)
        {
            AppendBounded(_history, text);
            AppendBounded(_pending, text);
        }
    }

    private static void AppendBounded(StringBuilder buffer, string text)
    {
        buffer.Append(text);
        if (buffer.Length > BufferLimit)
            buffer.Remove(0, buffer.Length - BufferLimit);
    }

    private async Task RenderOutputAsync()
    {
        if (!_ready || _rendering || _closing)
            return;
        string chunk;
        lock (_outputGate)
        {
            if (_pending.Length == 0)
                return;
            var count = Math.Min(_pending.Length, 32 * 1024);
            if (count < _pending.Length && char.IsHighSurrogate(_pending[count - 1]))
                count--;
            chunk = _pending.ToString(0, count);
            _pending.Remove(0, count);
        }
        _rendering = true;
        var token = _documentToken;
        try
        {
            if (!await SendToViewAsync(token, "write", chunk) && _documentToken == token)
            {
                lock (_outputGate)
                {
                    _pending.Insert(0, chunk);
                    if (_pending.Length > BufferLimit)
                        _pending.Remove(0, _pending.Length - BufferLimit);
                }
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task<bool> SendToViewAsync(string token, string action, string? text = null)
    {
        try
        {
            await TerminalView.InvokeScript(
                $"window.terminalAction?.({JsonSerializer.Serialize(token)}, {JsonSerializer.Serialize(action)}, {JsonSerializer.Serialize(text)})");
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static string ReadAsset(string name)
    {
        using var stream = typeof(InteractiveTerminalView).Assembly
            .GetManifestResourceStream($"SupraChat.TerminalAssets.{name}")
            ?? throw new InvalidOperationException($"Missing bundled terminal asset {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
