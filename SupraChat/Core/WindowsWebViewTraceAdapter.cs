using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SupraChat.Core;

/// <summary>
/// Passive native metadata adapter. Admission currently covers fresh synthetic loopback views only.
/// This class has no signed-in ChatGPT attachment or arbitrary CDP-command API.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWebViewTraceAdapter : IAsyncDisposable, ILocalTraceObserver
{
    private static readonly object OwnersGate = new();
    private static readonly Dictionary<IntPtr, WindowsWebViewTraceAdapter> Owners = new();
    private readonly CoreWebView2 _core;
    private readonly ITraceSink _sink;
    private readonly LocalTraceSession _session;
    private readonly Uri _origin;
    private readonly Func<bool>? _admissionStillActive;
    private readonly HashSet<string> _requests = new(StringComparer.Ordinal);
    private readonly List<(CoreWebView2DevToolsProtocolEventReceiver Receiver,
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> Handler)> _subscriptions = new();
    private bool _stopped;
    private IntPtr _identity;
    private Task? _enableTask;
    private Task? _releaseTask;
    public long DroppedObservations { get; private set; }
    public long ObservedEvents { get; private set; }
    public bool IsStopped => _stopped;

    private WindowsWebViewTraceAdapter(CoreWebView2 core, ITraceSink sink, LocalTraceSession session, Uri origin, Func<bool>? admission)
    { _core = core; _sink = sink; _session = session; _origin = origin; _admissionStillActive = admission; }

    public static Task<WindowsWebViewTraceAdapter> AttachSyntheticAsync(IntPtr nativeHandle,
        TraceNormalizer sink, LocalTraceSession session, Uri origin) =>
        AttachCoreAsync(nativeHandle, sink, session, origin, sink, null);

    private static async Task<WindowsWebViewTraceAdapter> AttachCoreAsync(IntPtr nativeHandle,
        TraceNormalizer normalizer, LocalTraceSession session, Uri origin, ITraceSink sink, Func<bool>? admission)
    {
        if (!origin.IsAbsoluteUri || !origin.IsLoopback || origin.Scheme is not ("http" or "https") ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new UnauthorizedAccessException("Synthetic native observer origin refused.");
        normalizer.Read(session, limit: 1); // A UUID or a capability from another normalizer is not admission.
        if (nativeHandle == IntPtr.Zero) throw new ArgumentException("Native observer handle unavailable.");
        var core = CoreWebView2.CreateFromComICoreWebView2(nativeHandle);
        var current = core.Source;
        if (current != "about:blank" && (!Uri.TryCreate(current, UriKind.Absolute, out var currentUri) || !SameOrigin(currentUri, origin)))
            throw new UnauthorizedAccessException("Native observer requires a blank or owned synthetic view.");
        var adapter = new WindowsWebViewTraceAdapter(core, sink, session, origin, admission);
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        if (Marshal.QueryInterface(nativeHandle, in unknown, out var identity) < 0 || identity == IntPtr.Zero)
            throw new InvalidOperationException("Native identity unavailable.");
        lock (OwnersGate)
        {
            if (Owners.ContainsKey(identity) || Owners.Count >= 16)
            {
                Marshal.Release(identity);
                throw new UnauthorizedAccessException("Native observation domain already owned or capacity reached.");
            }
            adapter._identity = identity;
            Owners.Add(identity, adapter);
        }
        try
        {
            foreach (var kind in new[] { "requestWillBeSent", "responseReceived", "dataReceived", "loadingFinished", "loadingFailed",
                "webSocketCreated", "webSocketFrameSent", "webSocketFrameReceived", "webSocketClosed", "eventSourceMessageReceived" })
            {
                var receiver = core.GetDevToolsProtocolEventReceiver("Network." + kind);
                EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, args) => adapter.OnEvent(kind, args.ParameterObjectAsJson);
                receiver.DevToolsProtocolEventReceived += handler;
                adapter._subscriptions.Add((receiver, handler));
            }
            core.NavigationStarting += adapter.NavigationStarting;
            adapter._enableTask = core.CallDevToolsProtocolMethodAsync("Network.enable",
                "{\"maxTotalBufferSize\":65536,\"maxResourceBufferSize\":16384,\"maxPostDataSize\":0}");
            await adapter._enableTask.WaitAsync(TimeSpan.FromSeconds(5));
            return adapter;
        }
        catch
        {
            adapter.Stop();
            adapter.Gap();
            throw new InvalidOperationException("Native observer could not attach; capture is incomplete.");
        }
    }

    private static bool SameOrigin(Uri value, Uri origin) => value.Scheme == origin.Scheme &&
        value.Host == origin.Host && value.Port == origin.Port && value.UserInfo.Length == 0;

    private bool Admits(Uri uri) => SameOrigin(uri, _origin) ||
        (uri.Scheme == (_origin.Scheme == "https" ? "wss" : "ws") && uri.Host == _origin.Host && uri.Port == _origin.Port && uri.UserInfo.Length == 0);
    private void RevokeRequest(string id)
    {
        if (_requests.Remove(id)) Gap();
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // Do not block or rewrite navigation. Detach before a view leaves the admitted fixture origin.
        try
        {
            if (args.Uri == "about:blank") return;
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var next) || !SameOrigin(next, _origin))
            { Gap(); Stop(); }
        }
        catch { Gap(); Stop(); }
    }

    private void OnEvent(string name, string json)
    {
        if (_stopped) return;
        if (json.Length > 1024 * 1024) { Gap(); return; }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("requestId", out var idElement) || idElement.ValueKind != JsonValueKind.String) { Gap(); return; }
            var id = idElement.GetString()!;
            if (id.Length > 512) { Gap(); return; }
            if (name is "requestWillBeSent" or "webSocketCreated")
            {
                var url = name == "requestWillBeSent" ? root.GetProperty("request").GetProperty("url").GetString() : root.GetProperty("url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { RevokeRequest(id); Gap(); return; }
                // WebSocket schemes share the admitted fixture host/port, never an inferred conversation.
                if (!Admits(uri)) { RevokeRequest(id); return; }
                if (_requests.Count >= 256 && !_requests.Contains(id)) { Gap(); return; }
                _requests.Add(id);
            }
            if (!_requests.Contains(id)) return;
            if (name == "responseReceived")
            {
                var response = root.GetProperty("response");
                if (!response.TryGetProperty("url", out var responseUrl) || responseUrl.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(responseUrl.GetString(), UriKind.Absolute, out var responseUri) || !Admits(responseUri))
                { RevokeRequest(id); return; }
            }
            var kind = name switch
            {
                "requestWillBeSent" or "webSocketCreated" => TraceEventKind.Request,
                "responseReceived" => TraceEventKind.Response,
                "loadingFinished" or "webSocketClosed" => TraceEventKind.Completed,
                "loadingFailed" => TraceEventKind.Error,
                _ => TraceEventKind.Chunk
            };
            var direction = name is "requestWillBeSent" or "webSocketCreated" or "webSocketFrameSent" ? TraceDirection.Egress : TraceDirection.Ingress;
            int? status = null;
            long bytes = 0;
            if (name == "responseReceived" && root.GetProperty("response").TryGetProperty("status", out var statusElement) &&
                statusElement.TryGetInt32(out var code) && code is >= 100 and <= 599) status = code;
            if (name == "dataReceived" && root.TryGetProperty("dataLength", out var size) && size.TryGetInt64(out var count) && count >= 0) bytes = count;
            Emit(new TraceObservation(TraceSurface.Synthetic, TraceAdapter.WebView2, direction, kind,
                _origin.AbsoluteUri, id, id, ByteCount: bytes, Status: status));
            if (name is "loadingFinished" or "loadingFailed" or "webSocketClosed") _requests.Remove(id);
        }
        catch { Gap(); }
    }

    private void Emit(TraceObservation observation)
    {
        try { if (_sink.TryObserve(_session, observation)) ObservedEvents++; }
        catch (UnauthorizedAccessException) { DroppedObservations++; Stop(); }
        catch (ObjectDisposedException) { DroppedObservations++; Stop(); }
        catch { DroppedObservations++; }
    }
    private void Gap()
    {
        DroppedObservations++;
        Emit(new TraceObservation(TraceSurface.Synthetic, TraceAdapter.WebView2, TraceDirection.Local, TraceEventKind.CaptureGap));
    }
    public void StopObserving() => Stop();
    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _core.NavigationStarting -= NavigationStarting; } catch { Gap(); }
        foreach (var subscription in _subscriptions)
            try { subscription.Receiver.DevToolsProtocolEventReceived -= subscription.Handler; } catch { Gap(); }
        _subscriptions.Clear();
        _requests.Clear();
        _releaseTask ??= ReleaseDomainAsync();
    }
    private async Task ReleaseDomainAsync()
    {
        try
        {
            if (_enableTask is not null) { try { await _enableTask; } catch { Gap(); } }
            try { await _core.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); } catch { Gap(); }
        }
        finally
        {
            lock (OwnersGate)
            {
                if (_identity != IntPtr.Zero)
                {
                    Owners.Remove(_identity);
                    Marshal.Release(_identity);
                    _identity = IntPtr.Zero;
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Stop();
        try { if (_releaseTask is not null) await _releaseTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { Gap(); } // Pending disable retains exclusive ownership until the command actually settles.
    }
}
