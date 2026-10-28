using System.Text.Json;

namespace SupraChat.Core;

/// <summary>Shared production parser, independently testable with synthetic JSON. Raw JSON is never retained.</summary>
internal sealed class NativeMetadataEventProcessor(Uri origin, TraceSurface surface, ITraceSink sink,
    LocalTraceSession session, NativeCaptureDiagnostics diagnostics, Action stop)
{
    private readonly HashSet<string> _requests = new(StringComparer.Ordinal);
    internal long ObservedEvents { get; private set; }
    internal long ObservedNetworkEvents { get; private set; }
    internal void Clear() => _requests.Clear();
    private bool Admits(Uri uri) => (uri.Scheme == origin.Scheme || uri.Scheme == (origin.Scheme == "https" ? "wss" : "ws")) &&
        uri.Host == origin.Host && uri.Port == origin.Port && uri.UserInfo.Length == 0;
    private void Revoke(string id) { if (_requests.Remove(id)) Gap(NativeDiagnosticReason.RequestScopeRevoked); }
    internal void Process(string name, string json)
    {
        // This bound precedes origin filtering: an off-origin raw event can cause this incident.
        if (json.Length > 1024 * 1024) { Gap(NativeDiagnosticReason.RawEventOversize); return; }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("requestId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            { Gap(NativeDiagnosticReason.MissingRequestId); return; }
            var id = idElement.GetString()!;
            if (id.Length > 512) { Gap(NativeDiagnosticReason.RequestIdOversize); return; }
            if (name is "requestWillBeSent" or "webSocketCreated")
            {
                var url = name == "requestWillBeSent" ? root.GetProperty("request").GetProperty("url").GetString() : root.GetProperty("url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { Revoke(id); Gap(NativeDiagnosticReason.InvalidRequestUrl); return; }
                if (!Admits(uri)) { Revoke(id); return; }
                if (_requests.Count >= 256 && !_requests.Contains(id)) { Gap(NativeDiagnosticReason.CorrelationCapacity); return; }
                _requests.Add(id);
            }
            if (!_requests.Contains(id)) return;
            if (name == "responseReceived")
            {
                var response = root.GetProperty("response");
                if (!response.TryGetProperty("url", out var responseUrl) || responseUrl.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(responseUrl.GetString(), UriKind.Absolute, out var responseUri) || !Admits(responseUri))
                { Revoke(id); return; }
            }
            var kind = name switch
            {
                "requestWillBeSent" or "webSocketCreated" => TraceEventKind.Request,
                "responseReceived" => TraceEventKind.Response,
                "loadingFinished" or "webSocketClosed" => TraceEventKind.Completed,
                "loadingFailed" => TraceEventKind.Error,
                "dataReceived" or "webSocketFrameSent" or "webSocketFrameReceived" or "eventSourceMessageReceived" => TraceEventKind.Chunk,
                _ => throw new InvalidOperationException()
            };
            var direction = name is "requestWillBeSent" or "webSocketCreated" or "webSocketFrameSent" ? TraceDirection.Egress : TraceDirection.Ingress;
            int? status = null; long bytes = 0;
            if (name == "responseReceived" && root.GetProperty("response").TryGetProperty("status", out var statusElement) &&
                statusElement.TryGetInt32(out var code) && code is >= 100 and <= 599) status = code;
            if (name == "dataReceived" && root.TryGetProperty("dataLength", out var size) && size.TryGetInt64(out var count) && count >= 0) bytes = count;
            Emit(new(surface, TraceAdapter.WebView2, direction, kind, origin.AbsoluteUri, id, id, ByteCount: bytes, Status: status));
            if (name is "loadingFinished" or "loadingFailed" or "webSocketClosed") _requests.Remove(id);
        }
        catch { Gap(NativeDiagnosticReason.MetadataMalformed); }
    }
    internal void Gap(NativeDiagnosticReason reason)
    {
        diagnostics.Record(reason);
        Emit(new(surface, TraceAdapter.WebView2, TraceDirection.Local, TraceEventKind.CaptureGap));
    }
    private void Emit(TraceObservation observation)
    {
        try
        {
            if (!sink.TryObserve(session, observation)) { diagnostics.Record(NativeDiagnosticReason.SinkRefused); return; }
            ObservedEvents++;
            if (observation.Direction != TraceDirection.Local && observation.EventKind != TraceEventKind.CaptureGap) ObservedNetworkEvents++;
        }
        catch (UnauthorizedAccessException) { diagnostics.Record(NativeDiagnosticReason.SinkUnauthorized); stop(); }
        catch (ObjectDisposedException) { diagnostics.Record(NativeDiagnosticReason.SinkDisposed); stop(); }
        catch { diagnostics.Record(NativeDiagnosticReason.SinkFailure); }
    }
}
