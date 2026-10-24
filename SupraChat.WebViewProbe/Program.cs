using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using System.Text.Json;
using System.Runtime.CompilerServices;
using SupraChat.Core;
using Microsoft.Web.WebView2.Core;

namespace SupraChat.WebViewProbe;

internal static class Program
{
    internal static string ReceiptPath = "";
    internal static int Result = 1;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] != "--receipt") return 64;
        ReceiptPath = Path.GetFullPath(args[1]);
        if (new[] { "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER", "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER" }.Any(name =>
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
        {
            WriteReceipt(new { schema = "suprachat-webview-probe/v1", evidence = "SYNTHETIC", passed = false, phase = "environment-override-refused" });
            return 64;
        }
        try { AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); }
        catch { WriteReceipt(new { schema = "suprachat-webview-probe/v1", evidence = "SYNTHETIC", passed = false, phase = "startup" }); }
        return Result;
    }

    internal static void WriteReceipt(object receipt)
    {
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        if (json.Contains(FixtureServer.Sentinel, StringComparison.Ordinal)) throw new InvalidOperationException("Receipt privacy check failed.");
        Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)!);
        File.WriteAllText(ReceiptPath, json);
    }
}

internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Title = "SupraChat synthetic native WebView probe", Width = 1000, Height = 600 };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
            var profileRoot = Path.Combine(Path.GetTempPath(), "suprachat-webview-probe", Guid.NewGuid().ToString("N"));
            var views = new[] { new NativeWebView(), new NativeWebView(), new NativeWebView() };
            var created = new[] { new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource() };
            for (var i = 0; i < views.Length; i++)
            {
                var index = i;
                views[i].EnvironmentRequested += (_, args) =>
                {
                    args.EnableDevTools = false;
                    if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
                    {
                        windows.UserDataFolder = Path.Combine(profileRoot, index.ToString());
                        windows.ProfileName = "SyntheticProbe";
                        windows.IsInPrivateModeEnabled = true;
                        windows.AdditionalBrowserArguments = "--disable-background-networking";
                    }
                };
                views[i].AdapterCreated += (_, _) => created[index].TrySetResult();
                Grid.SetColumn(views[i], i);
                grid.Children.Add(views[i]);
            }
            window.Content = grid;
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await RunAsync(desktop, views, created);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, NativeWebView[] views, TaskCompletionSource[] created)
    {
        var phase = "adapter-created";
        var observers = new List<NativeObserver>();
        var sharedAdapters = new List<WindowsWebViewTraceAdapter>();
        using var sharedTrace = new TraceNormalizer();
        var traceSessions = new List<LocalTraceSession>();
        var passed = false;
        object? details = null;
        string? failedCheck = null;
        FixtureServer? server = null;
        try
        {
            await Task.WhenAll(created.Select(c => c.Task)).WaitAsync(TimeSpan.FromSeconds(15));
            server = new FixtureServer();
            phase = "native-handle-attach";
            for (var i = 0; i < 2; i++)
            {
                if (views[i].TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle || handle.CoreWebView2 == IntPtr.Zero)
                    throw new InvalidOperationException("Native handle unavailable.");
                var observer = new NativeObserver(handle.CoreWebView2, server.Origin, server.Nonce, i == 0 ? "a" : "b");
                observers.Add(observer);
                await observer.AttachAsync();
                Require(observer.DevToolsDisabled);
                var session = sharedTrace.CreateSession();
                sharedTrace.SetCapture(session, TraceCaptureMode.Metadata);
                traceSessions.Add(session);
                sharedAdapters.Add(await WindowsWebViewTraceAdapter.AttachSyntheticAsync(handle.CoreWebView2, sharedTrace, session, server.Origin));
            }

            phase = "synthetic-network-observed";
            views[0].Navigate(server.Page("a", "observed"));
            views[1].Navigate(server.Page("b", "observed"));
            await WaitForFixturesAsync(observers, "observed");
            // Let end/cancellation and native resource notifications drain after script completion.
            var terminalDeadline = DateTime.UtcNow.AddSeconds(5);
            while (observers.Any(o => o.Counts.GetValueOrDefault("loadingFinished:stream") == 0 &&
                       o.Counts.GetValueOrDefault("loadingFailed:stream") == 0) && DateTime.UtcNow < terminalDeadline)
                await Task.Delay(40);
            foreach (var observer in observers)
            {
                foreach (var route in new[] { "page", "stream", "events", "cancel", "ack" })
                {
                    Require(observer.Counts.GetValueOrDefault("requestWillBeSent:" + route) == 1);
                    Require(observer.Counts.GetValueOrDefault("responseReceived:" + route) == 1);
                    if (observer.CompareResources)
                    {
                        Require(observer.Counts.GetValueOrDefault("resourceRequested:" + route) == 1);
                        Require(observer.Counts.GetValueOrDefault("resourceResponse:" + route) == 1);
                    }
                }
                foreach (var key in new[] { "dataReceived:stream", "loadingFailed:cancel",
                    "webSocketCreated:socket", "webSocketFrameSent:socket", "webSocketFrameReceived:socket", "eventSourceMessageReceived:events" })
                    Require(observer.Counts.GetValueOrDefault(key) > 0, "event:" + key);
                // The trace contract admits terminal end OR error. Preserve the observed browser
                // discrepancy instead of manufacturing provider-success semantics from page success.
                Require(observer.Counts.GetValueOrDefault("loadingFinished:stream") +
                    observer.Counts.GetValueOrDefault("loadingFailed:stream") == 1, "stream-terminal-event");
                Require(observer.Counts.GetValueOrDefault("dataReceived:stream") >= 1, "stream-data-observed");
                Require(!observer.CallbackFailed && observer.RequestAliases.Count == 6);
            }
            Require(!observers[0].RequestAliases.Overlaps(observers[1].RequestAliases));
            foreach (var session in traceSessions)
            {
                var page = sharedTrace.Read(session, limit: 256);
                Require(page.Events.Count >= 10 && page.Events.All(e => e.SessionId == session.SessionId &&
                    e.ProviderSurface == TraceSurface.Synthetic && e.Adapter == TraceAdapter.WebView2 && e.Outcome == TraceOutcome.Observed));
                Require(page.Events.Count(e => e.EventKind == TraceEventKind.Request) >= 6);
                Require(page.Events.Any(e => e.EventKind == TraceEventKind.Response && e.Status == 200));
                Require(!JsonSerializer.Serialize(page, TraceNormalizer.JsonOptions).Contains(FixtureServer.Sentinel));
            }
            Require(sharedAdapters.All(a => a.DroppedObservations == 0));
            foreach (var session in new[] { "a", "b" })
                foreach (var route in new[] { "page", "stream", "events", "cancel", "socket", "ack" })
                    Require(server.Count(session, "observed", route) == 1);

            phase = "detach-with-behavior-preserved";
            foreach (var adapter in sharedAdapters) await adapter.DisposeAsync();
            var typedEventsAtDetach = sharedAdapters.Select(a => a.ObservedEvents).ToArray();
            foreach (var observer in observers) await observer.DetachAsync();
            var callbacksAtDetach = observers.Select(o => o.TotalCallbacks).ToArray();
            views[0].Navigate(server.Page("a", "detached"));
            views[1].Navigate(server.Page("b", "detached"));
            await WaitForFixturesAsync(observers, "detached");
            await Task.Delay(400);
            for (var i = 0; i < observers.Count; i++)
            {
                Require(!observers[i].CallbackFailed && observers[i].TotalCallbacks == callbacksAtDetach[i]);
                Require(sharedAdapters[i].ObservedEvents == typedEventsAtDetach[i]);
            }
            foreach (var session in new[] { "a", "b" })
                foreach (var route in new[] { "page", "stream", "events", "cancel", "socket", "ack" })
                    Require(server.Count(session, "detached", route) == 1);

            phase = "scope-and-authority-negatives";
            if (views[2].TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle scopeHandle)
                throw new InvalidOperationException("Scope fixture handle unavailable.");
            using var scopeTrace = new TraceNormalizer();
            var scopeSession = scopeTrace.CreateSession();
            scopeTrace.SetCapture(scopeSession, TraceCaptureMode.Metadata);
            var wrongSessionRefused = false;
            try { await WindowsWebViewTraceAdapter.AttachSyntheticAsync(scopeHandle.CoreWebView2, scopeTrace, traceSessions[0], server.Origin); }
            catch (UnauthorizedAccessException) { wrongSessionRefused = true; }
            var firstPartyRefused = false;
            try { await WindowsWebViewTraceAdapter.AttachSyntheticAsync(scopeHandle.CoreWebView2, scopeTrace, scopeSession, new Uri("https://chatgpt.com/")); }
            catch (UnauthorizedAccessException) { firstPartyRefused = true; }
            Require(wrongSessionRefused && firstPartyRefused);
            phase = "native-domain-ownership";
            using var scopeOwner = new LocalTraceController();
            scopeOwner.Execute("start");
            await using var scopeAdapter = await scopeOwner.AttachSyntheticNativeAsync(scopeHandle.CoreWebView2, server.Origin);
            Require(scopeOwner.Execute("status").GetProperty("browser_capture").GetBoolean());
            var duplicateRefused = false;
            try { await scopeOwner.AttachSyntheticNativeAsync(scopeHandle.CoreWebView2, server.Origin); }
            catch (UnauthorizedAccessException) { duplicateRefused = true; }
            Require(duplicateRefused);
            scopeOwner.Dispose();
            Require(scopeAdapter.IsStopped);
            await scopeAdapter.DisposeAsync(); // Wait for the native domain to be released before replacement.
            using var replacementOwner = new LocalTraceController();
            replacementOwner.Execute("start");
            await using var replacement = await replacementOwner.AttachSyntheticNativeAsync(scopeHandle.CoreWebView2, server.Origin);
            await scopeAdapter.DisposeAsync(); // Repeated old disposal must not disable the replacement.
            var scopeCore = CoreWebView2.CreateFromComICoreWebView2(scopeHandle.CoreWebView2);
            phase = "cross-origin-redirect-revocation";
            await using var foreignServer = new FixtureServer();
            server.ForeignRedirectTarget = new Uri(foreignServer.Origin, $"{foreignServer.Nonce}/a/observed/foreign");
            views[2].Navigate(new Uri(server.Origin, $"{server.Nonce}/a/observed/redirect-page"));
            var redirectDeadline = DateTime.UtcNow.AddSeconds(8);
            while (scopeCore.DocumentTitle != "redirect-complete" && DateTime.UtcNow < redirectDeadline) await Task.Delay(40);
            Require(scopeCore.DocumentTitle == "redirect-complete");
            await Task.Delay(200);
            var redirectTrace = replacementOwner.Execute("read");
            Require(foreignServer.Count("a", "observed", "foreign") == 1);
            Require(replacement.ObservedEvents > 0 && replacement.DroppedObservations > 0);
            Require(redirectTrace.GetProperty("page").GetProperty("events").EnumerateArray().Any(e =>
                e.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 200));
            Require(!redirectTrace.GetProperty("page").GetProperty("events").EnumerateArray().Any(e =>
                e.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 201));
            phase = "navigation-scope-exit";
            views[2].Navigate(new Uri("data:text/html,synthetic-scope-exit"));
            var scopeDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!replacement.IsStopped && DateTime.UtcNow < scopeDeadline) await Task.Delay(40);
            Require(replacement.IsStopped && replacementOwner.Execute("read").GetProperty("capture_gap").GetBoolean());
            Require(!replacementOwner.Execute("status").GetProperty("browser_capture").GetBoolean());

            passed = true;
            phase = "verified";
            details = new
            {
                concurrent_sessions = 2,
                native_handle = true,
                devtools_disabled = true,
                cdp_network = true,
                resource_event_comparison = true,
                isolated_correlation = true,
                detached_callbacks = 0,
                fixture_behavior_preserved = true,
                progressive_stream_acknowledged = true,
                shared_trace_schema = "suprachat-trace/v1",
                typed_events = sharedAdapters.Select(a => a.ObservedEvents).ToArray(),
                typed_loss = sharedAdapters.Sum(a => a.DroppedObservations),
                scope_exit_detached = replacement.IsStopped,
                duplicate_domain_owner_refused = duplicateRefused,
                controller_disposal_detached = scopeAdapter.IsStopped,
                repeated_old_disposal_preserved_replacement = true,
                foreign_redirect_revoked = true,
                wrong_session_refused = wrongSessionRefused,
                first_party_admission_refused = firstPartyRefused,
                sessions = observers.Select((observer, index) => new { session = index, counts = observer.Counts }).ToArray()
            };
        }
        catch (ProbeAssertionException ex) { failedCheck = ex.Check; }
        catch { /* Fixed phase receipts only: no COM/URL/provider error details. */ }
        finally
        {
            foreach (var adapter in sharedAdapters) { try { await adapter.DisposeAsync(); } catch { } }
            foreach (var observer in observers) { try { observer.Dispose(); } catch { } }
            Program.Result = passed ? 0 : 1;
            Program.WriteReceipt(new
            {
                schema = "suprachat-webview-probe/v1", evidence = "WEBVIEW_NATIVE_OBSERVED",
                fixture = "SYNTHETIC_LOOPBACK_ONLY", passed, phase, failed_check = failedCheck, details,
                qualification_scope = "NATIVE_SHARED_TRACE_SYNTHETIC_ONLY",
                completeness = "UNKNOWN", production_admitted = false,
                stream_lifecycle_disagreement = observers.Any(o => o.Counts.GetValueOrDefault("loadingFailed:stream") > 0),
                observations = observers.Select((observer, index) => new
                {
                    session = index, counts = observer.Counts, alias_count = observer.RequestAliases.Count,
                    callback_failed = observer.CallbackFailed, total_callbacks = observer.TotalCallbacks, resource_comparison = observer.CompareResources,
                    fixture_complete = observer.FixtureComplete("observed") || observer.FixtureComplete("detached"), fixture_failed = observer.FixtureFailed,
                    runtime_version = observer.RuntimeVersion
                }).ToArray(),
                real_credentials = false, real_chatgpt_traffic = false, raw_payload_exported = false,
                remote_debugging_listener = false
            });
            if (server is not null) await server.DisposeAsync();
            desktop.Shutdown(Program.Result);
        }
    }

    private static async Task WaitForFixturesAsync(List<NativeObserver> observers, string phase)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (observers.Any(o => o.FixtureFailed)) throw new InvalidOperationException("Synthetic fixture failed.");
            if (observers.All(o => o.FixtureComplete(phase))) return;
            await Task.Delay(40);
        }
        throw new TimeoutException("Synthetic fixture did not complete.");
    }

    private static void Require(bool condition, [CallerArgumentExpression(nameof(condition))] string check = "")
    {
        if (!condition) throw new ProbeAssertionException(check);
    }

    private sealed class ProbeAssertionException(string check) : Exception
    {
        public string Check { get; } = check;
    }
}
