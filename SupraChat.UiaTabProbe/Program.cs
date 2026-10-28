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
            args[1] is not ("placeholder" or "webview")) return 64;
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
            var secondButton = Named(new Button { Content = "Synthetic second tab button" }, "Synthetic.TabProbe.SecondButton");
            var first = Named(new TabItem { Header = "First" }, "Synthetic.TabProbe.FirstTab");
            var second = Named(new TabItem { Header = "Second", Content = secondButton }, "Synthetic.TabProbe.SecondTab");
            var tabs = Named(new TabControl { ItemsSource = new[] { first, second }, SelectedIndex = 0 }, "Synthetic.TabProbe.Tabs");
            var window = Named(new Window
            {
                Title = "SupraChat disposable synthetic tab probe",
                Width = 800, Height = 500,
                Content = new DockPanel { Children = { toolbar, ready, tabs } }
            }, "SupraChat.SyntheticTabProbe");
            DockPanel.SetDock(toolbar, Dock.Top);
            DockPanel.SetDock(ready, Dock.Top);
            LoopbackPage? page = null;
            if (Program.Variant == "webview")
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
                view.AdapterCreated += (_, _) =>
                {
                    page ??= new LoopbackPage();
                    view.Navigate(page.Origin);
                };
                view.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess && page is not null && args.Request == page.Origin)
                        ready.Text = "NATIVE_PAGE_READY";
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
    public Uri Origin { get; }
    public LoopbackPage()
    {
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
                _ = await stream.ReadAsync(new byte[4096]).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                var body = "<!doctype html><title>Synthetic tab probe</title><p>Loopback fixture only</p>";
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\nContent-Security-Policy: default-src 'none'\r\n\r\n{body}");
                await stream.WriteAsync(response);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (IOException) { }
        catch (TimeoutException) { }
    }
    public void Dispose() => _listener.Stop();
}
