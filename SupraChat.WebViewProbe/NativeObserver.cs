using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SupraChat.WebViewProbe;

internal sealed class NativeObserver : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly Uri _origin;
    private readonly string _pathPrefix;
    private readonly Dictionary<string, string> _requests = new();
    private readonly List<(CoreWebView2DevToolsProtocolEventReceiver Receiver,
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> Handler)> _subscriptions = new();
    private bool _disposed;
    private bool _resourceAttached;
    public Dictionary<string, int> Counts { get; } = new();
    public HashSet<string> RequestAliases { get; } = new();
    public bool CallbackFailed { get; private set; }
    public bool DevToolsDisabled => !_core.Settings.AreDevToolsEnabled;
    public int TotalCallbacks { get; private set; }
    public bool FixtureComplete(string phase) => _core.DocumentTitle == "fixture-complete-" + phase;
    public bool FixtureFailed => _core.DocumentTitle == "fixture-failed";
    public string RuntimeVersion => _core.Environment.BrowserVersionString;
    public bool CompareResources { get; }
    private readonly bool _ownsNetworkDomain;

    public NativeObserver(IntPtr handle, Uri origin, string nonce, string session, bool compareResources = true, bool ownsNetworkDomain = false)
    {
        _origin = origin;
        _pathPrefix = $"/{nonce}/{session}/observed/";
        _core = CoreWebView2.CreateFromComICoreWebView2(handle);
        CompareResources = compareResources;
        _ownsNetworkDomain = ownsNetworkDomain;
    }

    public async Task AttachAsync()
    {
        foreach (var name in new[] { "requestWillBeSent", "responseReceived", "dataReceived", "loadingFinished", "loadingFailed",
            "webSocketCreated", "webSocketFrameSent", "webSocketFrameReceived", "eventSourceMessageReceived" })
        {
            var receiver = _core.GetDevToolsProtocolEventReceiver("Network." + name);
            EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, args) => OnCdp(name, args.ParameterObjectAsJson);
            receiver.DevToolsProtocolEventReceived += handler;
            _subscriptions.Add((receiver, handler));
        }
        if (CompareResources)
        {
            _core.AddWebResourceRequestedFilter(_origin.AbsoluteUri + "*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnResourceRequest;
            _core.WebResourceResponseReceived += OnResourceResponse;
            _resourceAttached = true;
        }
        if (_ownsNetworkDomain) await _core.CallDevToolsProtocolMethodAsync("Network.enable", "{}").WaitAsync(TimeSpan.FromSeconds(5));
    }

    private string? Route(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != _origin.Host || uri.Port != _origin.Port ||
            uri.Scheme is not ("http" or "ws") || !uri.AbsolutePath.StartsWith(_pathPrefix, StringComparison.Ordinal)) return null;
        var route = uri.AbsolutePath[_pathPrefix.Length..];
        return route is "page" or "stream" or "events" or "cancel" or "socket" or "ack" ? route : null;
    }

    private void Count(string key) => Counts[key] = Counts.GetValueOrDefault(key) + 1;

    private void OnCdp(string name, string json)
    {
        TotalCallbacks++;
        if (_disposed) { CallbackFailed = true; return; }
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (!root.TryGetProperty("requestId", out var idNode)) return;
            var id = idNode.GetString()!;
            if (name is "requestWillBeSent" or "webSocketCreated")
            {
                var url = name == "requestWillBeSent" ? root.GetProperty("request").GetProperty("url").GetString()! : root.GetProperty("url").GetString()!;
                var route = Route(url);
                if (route is null) return;
                if (_requests.Count >= 64 && !_requests.ContainsKey(id)) { CallbackFailed = true; return; }
                _requests[id] = route;
                // Synthetic-only, run-local identifiers; receipts export only alias counts.
                RequestAliases.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_pathPrefix + id))));
            }
            if (_requests.TryGetValue(id, out var knownRoute))
            {
                Count(name + ":" + knownRoute);
                if (name == "loadingFailed")
                {
                    // Closed enum of synthetic diagnostic categories; never export arbitrary error text.
                    var error = root.GetProperty("errorText").GetString();
                    var category = error switch
                    {
                        "net::ERR_ABORTED" => "aborted",
                        "net::ERR_INCOMPLETE_CHUNKED_ENCODING" => "incomplete-chunks",
                        "net::ERR_CONNECTION_CLOSED" => "connection-closed",
                        _ => "other"
                    };
                    Count("failure-" + category + ":" + knownRoute);
                }
            }
        }
        catch { CallbackFailed = true; }
    }

    private void OnResourceRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        TotalCallbacks++;
        if (_disposed) { CallbackFailed = true; return; }
        try { if (Route(args.Request.Uri) is { } route) Count("resourceRequested:" + route); }
        catch { CallbackFailed = true; }
    }

    private void OnResourceResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        TotalCallbacks++;
        if (_disposed) { CallbackFailed = true; return; }
        try { if (Route(args.Request.Uri) is { } route) Count("resourceResponse:" + route); }
        catch { CallbackFailed = true; }
    }

    public async Task DetachAsync()
    {
        Dispose();
        if (_ownsNetworkDomain) await _core.CallDevToolsProtocolMethodAsync("Network.disable", "{}").WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var subscription in _subscriptions)
            subscription.Receiver.DevToolsProtocolEventReceived -= subscription.Handler;
        _subscriptions.Clear();
        if (_resourceAttached)
        {
            _core.WebResourceRequested -= OnResourceRequest;
            _core.WebResourceResponseReceived -= OnResourceResponse;
            _core.RemoveWebResourceRequestedFilter(_origin.AbsoluteUri + "*", CoreWebView2WebResourceContext.All);
        }
        _disposed = true;
        // Avalonia owns the native object's lifetime; never close or FinalReleaseComObject it here.
    }
}
