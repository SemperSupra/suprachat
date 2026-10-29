using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;

namespace SupraChat.UiaTabProbe;

internal static class Program
{
    internal static string Variant = "";

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] != "--variant" ||
            args[1] is not ("placeholder" or "webview" or "webview-pending")) return 64;
        if (new[] { "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER",
            "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER" }.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            return 64;
        Variant = args[1];
        try { AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); return 0; }
        catch { return 1; } // No browser/provider exception text.
    }
}

internal sealed class ProbeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var ready = Named(new TextBlock { Text = "WAITING" }, "Synthetic.TabProbe.Ready");
            var toolbar = Named(new Button { Content = "Synthetic toolbar" }, "Synthetic.TabProbe.Toolbar");
            var selectionWitness = Named(new TextBlock { Text = "NOT_APPLICABLE" }, "Synthetic.TabProbe.SelectionWitness");
            var secondButton = Named(new Button { Content = "Synthetic second tab button" }, "Synthetic.TabProbe.SecondButton");
            var first = Named(new TabItem { Header = "First" }, "Synthetic.TabProbe.FirstTab");
            var second = Named(new TabItem { Header = "Second", Content = secondButton }, "Synthetic.TabProbe.SecondTab");
            var tabs = Named(new TabControl { ItemsSource = new[] { first, second }, SelectedIndex = 0 }, "Synthetic.TabProbe.Tabs");
            var window = Named(new Window
            {
                Title = "SupraChat disposable synthetic tab probe",
                Width = 800, Height = 500,
                Content = new DockPanel { Children = { toolbar, ready, selectionWitness, tabs } }
            }, "SupraChat.SyntheticTabProbe");
            DockPanel.SetDock(toolbar, Dock.Top);
            DockPanel.SetDock(ready, Dock.Top);
            DockPanel.SetDock(selectionWitness, Dock.Top);
            LoopbackPage? page = null;
            var navigationCompleted = false;
            tabs.SelectionChanged += (_, _) =>
            {
                if (Program.Variant == "webview-pending" && tabs.SelectedIndex == 1)
                    selectionWitness.Text = page is not null && page.ResponseHeld && !navigationCompleted
                        ? "PENDING_AT_SELECTION" : "PENDING_NOT_PROVEN";
            };
            if (Program.Variant is "webview" or "webview-pending")
            {
                // Fresh synthetic-only profile. No product code, AppState,
                // existing profiles, accounts, CDP observer or provider calls.
                var profile = Path.Combine(Path.GetTempPath(), "suprachat-tab-probe", Guid.NewGuid().ToString("N"));
                var view = new NativeWebView();
                view.EnvironmentRequested += (_, args) =>
                {
                    args.EnableDevTools = false;
                    if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
                    {
                        windows.UserDataFolder = profile;
                        windows.ProfileName = "SyntheticTabProbe";
                        windows.IsInPrivateModeEnabled = true;
                        windows.AdditionalBrowserArguments = "--disable-background-networking";
                    }
                };
                view.AdapterCreated += async (_, _) =>
                {
                    try
                    {
                        var pending = Program.Variant == "webview-pending";
                        page ??= new LoopbackPage(pending);
                        view.Navigate(page.Origin);
                        if (pending)
                        {
                            await page.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                            ready.Text = !navigationCompleted && page.ResponseHeld ? "NATIVE_NAVIGATION_PENDING" : "FIXTURE_REFUSED";
                        }
                    }
                    catch { ready.Text = "FIXTURE_REFUSED"; }
                };
                view.NavigationCompleted += (_, args) =>
                {
                    if (page is not null && page.RequestStarted.Task.IsCompleted)
                    {
                        navigationCompleted = true;
                        ready.Text = args.IsSuccess && args.Request == page.Origin ? "NATIVE_PAGE_READY" : "NATIVE_NAVIGATION_ENDED";
                    }
                };
                first.Content = view;
            }
            else
            {
                first.Content = new TextBlock { Text = "Synthetic placeholder" };
                ready.Text = "PLACEHOLDER_READY";
            }
            window.Closed += (_, _) => page?.Dispose();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static T Named<T>(T control, string id) where T : Control
    {
        AutomationProperties.SetAutomationId(control, id);
        return control;
    }
}

internal sealed class LoopbackPage : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly bool _holdResponse;
    private readonly CancellationTokenSource _stop = new();
    public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _responseHeld;
    public bool ResponseHeld => Volatile.Read(ref _responseHeld) == 1;
    public Uri Origin { get; }
    public LoopbackPage(bool holdResponse)
    {
        _holdResponse = holdResponse;
        _listener.Start();
        Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = ServeAsync();
    }
    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var received = await stream.ReadAsync(new byte[4096]).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                if (received <= 0) throw new IOException();
                if (_holdResponse) Volatile.Write(ref _responseHeld, 1);
                RequestStarted.TrySetResult();
                if (_holdResponse) await Task.Delay(Timeout.Infinite, _stop.Token);
                var body = "<!doctype html><title>Synthetic tab probe</title><p>Loopback fixture only</p>";
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\nContent-Security-Policy: default-src 'none'\r\n\r\n{body}");
                await stream.WriteAsync(response);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (IOException) { }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { Volatile.Write(ref _responseHeld, 0); _stop.Cancel(); _listener.Stop(); }
}
