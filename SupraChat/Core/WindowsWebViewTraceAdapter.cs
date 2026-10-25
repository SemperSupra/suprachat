using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SupraChat.Core;

/// <summary>
/// Passive native metadata adapter. Public attachment admits synthetic loopback views only.
/// The internal first-party route requires a current native consent lease; no arbitrary CDP-command API.
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
    private readonly TraceSurface _surface;
    private readonly Func<bool>? _captureAllowed;
    private readonly Action? _invalidate;
    private readonly HashSet<string> _requests = new(StringComparer.Ordinal);
    private readonly List<(CoreWebView2DevToolsProtocolEventReceiver Receiver,
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> Handler)> _subscriptions = new();
    private bool _stopped;
    private IntPtr _identity;
    private Task? _enableTask;
    private readonly NativeNetworkRegistration _registration = new();
    private Task? _releaseTask;
    public long DroppedObservations { get; private set; }
    public long ObservedEvents { get; private set; }
    public long ObservedNetworkEvents { get; private set; }
    public bool IsStopped => _stopped;

    private WindowsWebViewTraceAdapter(CoreWebView2 core, ITraceSink sink, LocalTraceSession session, Uri origin,
        TraceSurface surface, Func<bool>? captureAllowed = null, Action? invalidate = null)
    { _core = core; _sink = sink; _session = session; _origin = origin; _surface = surface; _captureAllowed = captureAllowed; _invalidate = invalidate; }

    public static async Task<WindowsWebViewTraceAdapter> AttachSyntheticAsync(IntPtr nativeHandle,
        TraceNormalizer sink, LocalTraceSession session, Uri origin)
    {
        sink.Read(session, limit: 1); // A UUID or a capability from another normalizer is not admission.
        return await AttachCoreAsync(nativeHandle, sink, session, origin, TraceSurface.Synthetic);
    }

    internal static Task<WindowsWebViewTraceAdapter> AttachConsentedAsync(IntPtr nativeHandle, ITraceSink sink,
        LocalTraceSession session, Uri origin, TraceSurface surface, Func<bool> allowed, Action invalidate,
        Action<WindowsWebViewTraceAdapter> pending) => AttachCoreAsync(nativeHandle, sink, session, origin, surface, allowed, invalidate, pending);

    internal static void ValidateOrigin(Uri origin, TraceSurface surface) => NativeMetadataOriginPolicy.Validate(origin, surface);

    private static async Task<WindowsWebViewTraceAdapter> AttachCoreAsync(IntPtr nativeHandle, ITraceSink sink,
        LocalTraceSession session, Uri origin, TraceSurface surface, Func<bool>? allowed = null, Action? invalidate = null,
        Action<WindowsWebViewTraceAdapter>? pending = null)
    {
        ValidateOrigin(origin, surface);
        if (surface == TraceSurface.ChatGptWebView && allowed is null) throw new UnauthorizedAccessException("First-party consent required.");
        if (allowed is not null && !allowed()) throw new UnauthorizedAccessException("Current native capture lease required.");
        if (nativeHandle == IntPtr.Zero) throw new ArgumentException("Native observer handle unavailable.");
        var core = CoreWebView2.CreateFromComICoreWebView2(nativeHandle);
        var current = core.Source;
        if (!(current == "about:blank" && surface == TraceSurface.Synthetic) &&
            (!Uri.TryCreate(current, UriKind.Absolute, out var currentUri) || !SameOrigin(currentUri, origin)))
            throw new UnauthorizedAccessException("Native observer requires a blank or owned synthetic view.");
        var adapter = new WindowsWebViewTraceAdapter(core, sink, session, origin, surface, allowed, invalidate);
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
            try
            {
            pending?.Invoke(adapter);
            if (adapter._stopped || allowed is not null && !allowed()) throw new UnauthorizedAccessException("Native consent revoked.");
            foreach (var kind in new[] { "requestWillBeSent", "responseReceived", "dataReceived", "loadingFinished", "loadingFailed",
                "webSocketCreated", "webSocketFrameSent", "webSocketFrameReceived", "webSocketClosed", "eventSourceMessageReceived" })
            {
                if (!adapter.AllowedNow()) throw new UnauthorizedAccessException("Native consent revoked during registration.");
                var receiver = core.GetDevToolsProtocolEventReceiver("Network." + kind);
                if (!adapter.AllowedNow()) throw new UnauthorizedAccessException("Native consent revoked during registration.");
                EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, args) =>
                {
                    // Gate BEFORE asking the SDK to materialize raw event JSON.
                    try
                    {
                        if (!adapter.AllowedNow()) { adapter.Stop(); return; }
                        adapter.OnEvent(kind, args.ParameterObjectAsJson);
                    }
                    catch { adapter.Gap(); adapter.Stop(); }
                };
                adapter._subscriptions.Add((receiver, handler));
                receiver.DevToolsProtocolEventReceived += handler;
                if (!adapter.AllowedNow())
                {
                    receiver.DevToolsProtocolEventReceived -= handler; // A reentrant Stop may have run before add completed.
                    adapter._subscriptions.Remove((receiver, handler));
                    throw new UnauthorizedAccessException("Native consent revoked during registration.");
                }
            }
            core.NavigationStarting += adapter.NavigationStarting;
            if (!adapter.AllowedNow())
            { core.NavigationStarting -= adapter.NavigationStarting; throw new UnauthorizedAccessException("Native consent revoked before enable."); }
            adapter._enableTask = core.CallDevToolsProtocolMethodAsync("Network.enable",
                "{\"maxTotalBufferSize\":65536,\"maxResourceBufferSize\":16384,\"maxPostDataSize\":0}");
            adapter._registration.Publish(adapter._enableTask);
            }
            finally { adapter._registration.Complete(); }
            await adapter._enableTask.WaitAsync(TimeSpan.FromSeconds(5));
            if (adapter._stopped || !adapter.AllowedNow()) throw new UnauthorizedAccessException("Native consent expired while attaching.");
            return adapter;
        }
        catch
        {
            adapter.Stop();
            adapter.Gap();
            throw new InvalidOperationException("Native observer could not attach; capture is incomplete.");
        }
    }

    private bool AllowedNow()
    { try { return !_stopped && (_captureAllowed?.Invoke() ?? true) && !_stopped; } catch { return false; } }

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
            if (_invalidate is not null) { _invalidate(); Stop(); return; }
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
            Emit(new TraceObservation(_surface, TraceAdapter.WebView2, direction, kind,
                _origin.AbsoluteUri, id, id, ByteCount: bytes, Status: status));
            if (name is "loadingFinished" or "loadingFailed" or "webSocketClosed") _requests.Remove(id);
        }
        catch { Gap(); }
    }

    private void Emit(TraceObservation observation)
    {
        try
        {
            if (_sink.TryObserve(_session, observation))
            {
                ObservedEvents++;
                if (observation.Direction != TraceDirection.Local && observation.EventKind != TraceEventKind.CaptureGap) ObservedNetworkEvents++;
            }
        }
        catch (UnauthorizedAccessException) { DroppedObservations++; Stop(); }
        catch (ObjectDisposedException) { DroppedObservations++; Stop(); }
        catch { DroppedObservations++; }
    }
    private void Gap()
    {
        DroppedObservations++;
        Emit(new TraceObservation(_surface, TraceAdapter.WebView2, TraceDirection.Local, TraceEventKind.CaptureGap));
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
            try { await _registration.WaitForEnableAsync(); } catch { Gap(); }
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
