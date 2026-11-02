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
    private static readonly object Sync = new();
    private static bool _written;

    internal static void WritePhase(string phase)
    {
        try { File.WriteAllText(Path.ChangeExtension(Receipt, ".phase.txt"), phase); }
        catch { /* Probe phase logging must never access credentials or mask the original failure. */ }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] != "--receipt") return 64;
        var rawPath = args[1].Trim('"', '\'');
        if (!Path.IsPathFullyQualified(rawPath)) return 64;
        Receipt = Path.GetFullPath(rawPath);
        if (File.Exists(Receipt)) return 64;

        if (new[] { "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER" }
            .Any(x => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(x))))
        {
            Write(new { schema = "suprachat-avalonia-extension-e2/v1", classification = "ENVIRONMENT_OVERRIDE_REFUSED",
                evidence = "SYNTHETIC_ONLY", production_admitted = false });
            return 64;
        }

        // Independent watchdog: hard abort at 45 seconds if UI thread or COM deadlocks
        using var watchdogCts = new CancellationTokenSource();
        var watchdog = Task.Delay(TimeSpan.FromSeconds(45), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                Write(new {
                    schema = "suprachat-avalonia-extension-e2/v1",
                    phase = "watchdog-timeout",
                    classification = "UNKNOWN_OR_FAILED_E2",
                    evidence = "SYNTHETIC_ONLY",
                    product_webview_changed = false,
                    production_admitted = false,
                    raw_payload_exported = false
                });
                Environment.Exit(1);
            }
        });

        try
        {
            AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Write(new {
                schema = "suprachat-avalonia-extension-e2/v1",
                classification = "AVALONIA_STARTUP_UNKNOWN",
                failure_type = ex.GetType().Name,
                evidence = "SYNTHETIC_ONLY"
            });
        }
        finally
        {
            watchdogCts.Cancel();
        }
        return ExitCode;
    }

    internal static void Write(object value)
    {
        lock (Sync)
        {
            if (_written && File.Exists(Receipt)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Receipt)!);
            var data = JsonSerializer.Serialize(value, new JsonSerializerOptions {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            using var output = new FileStream(Receipt, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(output);
            writer.Write(data);
            _written = true;
        }
    }
}

internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Title = "SupraChat disposable Avalonia E2", Width = 640, Height = 480 };
            var root = Path.Combine(Path.GetTempPath(), "suprachat-avalonia-e2", Guid.NewGuid().ToString("N"));
            var view = new NativeWebView();
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IntPtr environmentPointer = IntPtr.Zero;
            CoreWebView2Environment? managedEnvironment = null;
            string bridge = "UNOBSERVED";
            string browserVersion = "unknown";
            bool extensionsEnabled = false;

            view.EnvironmentRequested += async (_, request) =>
            {
                if (request is not WindowsWebView2EnvironmentRequestedEventArgs windows) return;
                Program.WritePhase("environment-requested");
                using var deferral = request.GetDeferral();
                try
                {
                    Program.WritePhase("before-create-environment");
                    var options = new CoreWebView2EnvironmentOptions
                    {
                        AreBrowserExtensionsEnabled = true,
                        AdditionalBrowserArguments = "--disable-background-networking"
                    };
                    managedEnvironment = await CoreWebView2Environment.CreateAsync(
                        null, Path.Combine(root, "profile"), options);
                    browserVersion = managedEnvironment.BrowserVersionString;
                    extensionsEnabled = true;

                    Program.WritePhase("acquire-native-environment");
                    environmentPointer = ComEnvironmentProbe.TryAcquire(managedEnvironment, out bridge);
                    if (environmentPointer == IntPtr.Zero)
                        throw new InvalidOperationException("Native environment interface unavailable.");

                    windows.EnableDevTools = false;
                    windows.ExplicitEnvironment = environmentPointer;
                    windows.ProfileName = "SyntheticE2";
                    windows.IsInPrivateModeEnabled = true;
                    Program.WritePhase("environment-deferral-complete");
                }
                catch (Exception ex)
                {
                    Program.WritePhase("environment-requested-failed");
                }
            };

            view.AdapterCreated += (_, _) =>
            {
                Program.WritePhase("adapter-created-event");
                created.TrySetResult();
            };

            window.Content = view;
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await ProbeAsync(desktop, window, view, created, root, () => environmentPointer, () => managedEnvironment, () => bridge, () => browserVersion, () => extensionsEnabled);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ProbeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window, NativeWebView view, TaskCompletionSource created,
        string root, Func<IntPtr> getEnvPtr, Func<CoreWebView2Environment?> getManagedEnv,
        Func<string> getBridge, Func<string> getBrowserVersion, Func<bool> getExtensionsEnabled)
    {
        Program.WritePhase("window-opened");
        string phase = "await-adapter-created";
        bool installed = false, adapterReady = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
        }
        finally
        {
            Program.Write(new {
                schema = "suprachat-avalonia-extension-e2/v1",
                phase,
                classification = Program.ExitCode == 0 ? "QUALIFIED_SYNTHETIC_E2" : "UNKNOWN_OR_FAILED_E2",
                bridge = getBridge(),
                runtime_version = getBrowserVersion(),
                extension_option = getExtensionsEnabled(),
                adapter_created = adapterReady,
                extension_installed = installed,
                evidence = "SYNTHETIC_ONLY",
                product_webview_changed = false,
                production_admitted = false,
                raw_payload_exported = false
            });
            Program.WritePhase("before-shutdown");
            window.Content = null;
            var ptr = getEnvPtr();
            if (ptr != IntPtr.Zero) Marshal.Release(ptr);
            var env = getManagedEnv();
            GC.KeepAlive(env);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            desktop.Shutdown(Program.ExitCode);
        }
    }
}
