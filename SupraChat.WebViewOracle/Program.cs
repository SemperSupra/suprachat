using System.Text.Json;
using Microsoft.Playwright;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SupraChat.WebViewProbe;

namespace SupraChat.WebViewOracle;

internal static class Program
{
    private static string _receipt = "";
    private static int _result = 1;
    private static string _phase = "startup";
    private static bool _retain;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is not (4 or 5) || args[0] != "--engine" || args[2] != "--receipt" || args[1] is not ("playwright" or "webview2") ||
            (args.Length == 5 && args[4] != "--retain")) return 64;
        _retain = args.Length == 5;
        _receipt = Path.GetFullPath(args[3]);
        if (File.Exists(_receipt)) return 64;
        if (new[] { "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER", "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER" }.Any(name =>
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
        { Save(args[1], false, "environment-override-refused", new Counters(), false, "unknown"); return 64; }
        try
        {
            if (args[1] == "playwright") RunPlaywrightAsync().GetAwaiter().GetResult();
            else RunMicrosoftHost();
        }
        catch (Exception ex)
        {
            var category = ex.Message.Contains("unsafe-eval", StringComparison.OrdinalIgnoreCase) ? "csp-eval-refused" :
                ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ? "timeout" :
                ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ? "target-closed" :
                ex is PlaywrightException ? "playwright-error" : "host-error";
            Save(args[1], false, _phase + ":" + category, new Counters(), false, "unknown");
        }
        return _result;
    }

    private static async Task RunPlaywrightAsync()
    {
        await using var fixture = new FixtureServer();
        _phase = "playwright-create";
        using var playwright = await Playwright.CreateAsync();
        _phase = "playwright-launch";
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var counts = new Counters();
        var target = fixture.Page("a", "observed", _retain);
        var cdp = await context.NewCDPSessionAsync(page);
        _phase = "playwright-observe";
        foreach (var name in new[] { "requestWillBeSent", "responseReceived", "dataReceived", "loadingFinished", "loadingFailed" })
        {
            cdp.Event("Network." + name).OnEvent += (_, value) =>
            {
                if (value is { } json) counts.Observe(name, json, target);
            };
        }
        page.RequestFinished += (_, request) => counts.HighLevel("finished", request.Url, target);
        page.RequestFailed += (_, request) => counts.HighLevel("failed", request.Url, target);
        _phase = "playwright-enable";
        await cdp.SendAsync("Network.enable");
        _phase = "playwright-navigate";
        await page.GotoAsync(target.AbsoluteUri);
        _phase = "playwright-fixture-wait";
        // Host-side polling preserves the fixture CSP; WaitForFunction's JS polling uses eval.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var title = await page.TitleAsync();
        while (DateTime.UtcNow < deadline && title is not ("fixture-complete-observed" or "fixture-failed"))
        { await Task.Delay(40); title = await page.TitleAsync(); }
        var complete = title == "fixture-complete-observed";
        _phase = "playwright-settle";
        await Task.Delay(500);
        Save("playwright-chromium", complete && counts.Valid, "observed", counts, complete, browser.Version);
    }

    private static void RunMicrosoftHost()
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = "SupraChat Microsoft-only synthetic oracle", Width = 700, Height = 500 };
        using var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            var counts = new Counters();
            try
            {
                await using var fixture = new FixtureServer();
                var target = fixture.Page("a", "observed", _retain);
                var folder = Path.Combine(Path.GetTempPath(), "suprachat-microsoft-oracle", Guid.NewGuid().ToString("N"));
                var environment = await CoreWebView2Environment.CreateAsync(null, folder);
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                await view.EnsureCoreWebView2Async(environment, options);
                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                var receivers = new List<CoreWebView2DevToolsProtocolEventReceiver>();
                foreach (var name in new[] { "requestWillBeSent", "responseReceived", "dataReceived", "loadingFinished", "loadingFailed" })
                {
                    var receiver = view.CoreWebView2.GetDevToolsProtocolEventReceiver("Network." + name);
                    receiver.DevToolsProtocolEventReceived += (_, e) =>
                    {
                        using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                        counts.Observe(name, doc.RootElement, target);
                    };
                    receivers.Add(receiver);
                }
                await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                view.CoreWebView2.Navigate(target.AbsoluteUri);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !view.CoreWebView2.DocumentTitle.StartsWith("fixture-complete-", StringComparison.Ordinal) &&
                       view.CoreWebView2.DocumentTitle != "fixture-failed") await Task.Delay(40);
                var complete = view.CoreWebView2.DocumentTitle == "fixture-complete-observed";
                await Task.Delay(500);
                Save("microsoft-webview2", complete && counts.Valid, "observed", counts, complete, environment.BrowserVersionString);
                GC.KeepAlive(receivers);
            }
            catch { Save("microsoft-webview2", false, "oracle-failed", counts, false, "unknown"); }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    private static void Save(string engine, bool passed, string phase, Counters counts, bool complete, string version)
    {
        var json = JsonSerializer.Serialize(new
        {
            schema = "suprachat-independent-browser-oracle/v1", engine, passed, phase,
            fixture = "SYNTHETIC_LOOPBACK_ONLY", fixture_complete = complete, runtime_version = version,
            response_retained = _retain,
            counts = counts.Snapshot(), production_admitted = false, raw_payload_exported = false
        }, new JsonSerializerOptions { WriteIndented = true });
        if (json.Contains(FixtureServer.Sentinel)) throw new InvalidOperationException("Oracle receipt privacy check failed.");
        Directory.CreateDirectory(Path.GetDirectoryName(_receipt)!);
        File.WriteAllText(_receipt, json);
        _result = passed ? 0 : 1;
    }

    private sealed class Counters
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _ids = new();
        private readonly Dictionary<string, int> _counts = new();
        public bool Valid { get { lock (_gate) return _counts.GetValueOrDefault("requestWillBeSent:stream") == 1 && _counts.GetValueOrDefault("dataReceived:stream") > 0; } }
        public Dictionary<string, int> Snapshot() { lock (_gate) return new(_counts); }
        private void Add(string key) => _counts[key] = _counts.GetValueOrDefault(key) + 1;
        private static string? Route(string url, Uri target)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Authority != target.Authority) return null;
            var prefix = target.AbsolutePath[..(target.AbsolutePath.LastIndexOf('/') + 1)];
            if (!parsed.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var route = parsed.AbsolutePath[prefix.Length..];
            return route is "page" or "stream" or "events" or "cancel" ? route : null;
        }
        public void HighLevel(string kind, string url, Uri target)
        {
            lock (_gate) if (Route(url, target) is { } route) Add("playwright-" + kind + ":" + route);
        }
        public void Observe(string name, JsonElement value, Uri target)
        {
            lock (_gate)
            {
                if (!value.TryGetProperty("requestId", out var idValue)) return;
                var id = idValue.GetString()!;
                if (name == "requestWillBeSent")
                {
                    var route = Route(value.GetProperty("request").GetProperty("url").GetString()!, target);
                    if (route is null || _ids.Count >= 32) return;
                    _ids[id] = route;
                }
                if (!_ids.TryGetValue(id, out var known)) return;
                Add(name + ":" + known);
                if (name == "loadingFailed")
                    Add((value.GetProperty("errorText").GetString() == "net::ERR_ABORTED" ? "aborted:" : "other-error:") + known);
            }
        }
    }
}
