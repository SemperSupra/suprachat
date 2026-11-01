using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace SupraChat.WebViewAvaloniaExtensionProbe;

// Disposable Windows E2. No first-party network or browser profile is accessed.
internal static class Program
{
    internal static string Receipt = "";
    internal static int ExitCode = 1;
    internal static void WritePhase(string phase)
    {
        try { File.WriteAllText(Path.ChangeExtension(Receipt, ".phase.txt"), phase); }
        catch { /* Probe phase logging must never access credentials or mask the original failure. */ }
    }
    internal static object Data = new { schema = "suprachat-avalonia-extension-e2/v1",
        classification = "NOT_STARTED", evidence = "SYNTHETIC_ONLY",
        production_admitted = false, raw_payload_exported = false };
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] != "--receipt" ||
            !Path.IsPathFullyQualified(args[1])) return 64;
        Receipt = Path.GetFullPath(args[1]);
        if (File.Exists(Receipt)) return 64;
        if (new[] { "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER" }
            .Any(x => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(x))))
        {
            Write(new { schema = "suprachat-avalonia-extension-e2/v1", classification = "ENVIRONMENT_OVERRIDE_REFUSED",
                evidence = "SYNTHETIC_ONLY", production_admitted = false });
            return 64;
        }
        try { AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex)
        {
            Write(new { schema = "suprachat-avalonia-extension-e2/v1",
                classification = "AVALONIA_STARTUP_UNKNOWN",
                failure_type = ex.GetType().Name, evidence = "SYNTHETIC_ONLY" });
        }
        return ExitCode;
    }
    internal static void Write(object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Receipt)!);
        var data = JsonSerializer.Serialize(value, new JsonSerializerOptions {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        using var output = new FileStream(Receipt, FileMode.Create);
        using var writer = new StreamWriter(output);
        writer.Write(data);
    }
}
internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Title = "SupraChat disposable Avalonia E2", Width = 640, Height = 480 };
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await ProbeAsync(window);
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static async Task ProbeAsync(Window window)
    {
        var root = Path.Combine(Path.GetTempPath(), "suprachat-avalonia-e2", Guid.NewGuid().ToString("N"));
        Program.WritePhase("window-opened");
        IntPtr environmentPointer = IntPtr.Zero;
        CoreWebView2Environment? managedEnvironment = null;
        string phase = "create-environment";
        string bridge = "UNOBSERVED";
        string browserVersion = "unknown";
        bool installed = false, adapterReady = false, extensionsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            Program.WritePhase("before-create-environment");
            var options = new CoreWebView2EnvironmentOptions {
                AreBrowserExtensionsEnabled = true,
                AdditionalBrowserArguments = "--disable-background-networking"
            };
            managedEnvironment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(root, "profile"), options).WaitAsync(timeout.Token);
            browserVersion = managedEnvironment.BrowserVersionString;
            extensionsEnabled = true;
            phase = "acquire-native-environment"; Program.WritePhase(phase);
            environmentPointer = ComEnvironmentProbe.TryAcquire(managedEnvironment, out bridge);
            if (environmentPointer == IntPtr.Zero)
                throw new InvalidOperationException("Native environment interface unavailable.");
            phase = "avalonia-environment-request"; Program.WritePhase(phase);
            var view = new NativeWebView();
            view.EnvironmentRequested += (_, request) =>
            {
                if (request is not WindowsWebView2EnvironmentRequestedEventArgs windows)
                    throw new InvalidOperationException("WebView2 backend not selected.");
                windows.EnableDevTools = false;
                windows.ExplicitEnvironment = environmentPointer;
                windows.ProfileName = "SyntheticE2";
                windows.IsInPrivateModeEnabled = true;
            };
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.AdapterCreated += (_, _) => created.TrySetResult();
            Program.WritePhase("before-attach-native-view");
            window.Content = view;
            Program.WritePhase("await-adapter-created");
            await created.Task.WaitAsync(timeout.Token);
            adapterReady = true;
            Program.WritePhase("adapter-created");
            if (view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle ||
                handle.CoreWebView2 == IntPtr.Zero)
                throw new InvalidOperationException("No native WebView2 handle.");
            phase = "profile-extension-install"; Program.WritePhase(phase);
            var extensionDir = Path.Combine(root, "extension");
            Directory.CreateDirectory(extensionDir);
            await File.WriteAllTextAsync(Path.Combine(extensionDir, "manifest.json"),
                "{\"manifest_version\":3,\"name\":\"Synthetic E2\",\"version\":\"1.0.0\"}",
                timeout.Token);
            var core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
            var extension = await core.Profile.AddBrowserExtensionAsync(extensionDir).WaitAsync(timeout.Token);
            var inventory = await core.Profile.GetBrowserExtensionsAsync().WaitAsync(timeout.Token);
            installed = inventory.Any(x => x.Id == extension.Id);
            if (!installed) throw new InvalidOperationException("Extension inventory mismatch.");
            phase = "synthetic-e2-complete"; Program.WritePhase(phase);
            Program.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Program.ExitCode = 1;
            if (ex is OperationCanceledException) phase = "timeout";
            // Raw exception messages can contain machine paths; export type only.
            Program.Data = new { failure_type = ex.GetType().Name };
        }
        finally
        {
            Program.Write(new {
                schema = "suprachat-avalonia-extension-e2/v1",
                phase,
                classification = Program.ExitCode == 0 ? "QUALIFIED_SYNTHETIC_E2" : "UNKNOWN_OR_FAILED_E2",
                bridge, runtime_version = browserVersion, extension_option = extensionsEnabled,
                adapter_created = adapterReady, extension_installed = installed,
                evidence = "SYNTHETIC_ONLY", product_webview_changed = false,
                production_admitted = false, raw_payload_exported = false
            });
            Program.WritePhase("before-window-close");
            window.Content = null;
            window.Close();
            Program.WritePhase("after-window-close");
            if (environmentPointer != IntPtr.Zero) Marshal.Release(environmentPointer);
            GC.KeepAlive(managedEnvironment);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}


