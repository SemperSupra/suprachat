using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SupraChat.Core;

internal static class OwnedTransportTraceChecks
{
    private const string Sentinel = "synthetic-transport-private-sentinel-51";
    private const string Delta = """{"type":"response.output_text.delta","delta":"synthetic-transport-private-sentinel-51-✓"}""";
    private const string Completed = """{"type":"response.completed"}""";
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        using var normalizer = new TraceNormalizer();
        var session = normalizer.CreateSession();
        var binding = new OwnedTransportTrace(normalizer, session);
        var sse = $"event: response.output_text.delta\ndata: {Delta}\n\ndata: {Completed}\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new SyntheticHttpHandler(sse));
        var responses = new ResponsesClient(http, binding);
        var raw = new List<string>();
        var output = await responses.StreamRawAsync(Sentinel, "synthetic-model", "{}", onEvent: (_, payload) => raw.Add(payload));
        Check(output.Completed && output.Text == Sentinel + "-✓" && raw.SequenceEqual(new[] { Delta, Completed }),
            "Tracing changed SSE payload/delta framing.");
        Check(normalizer.Read(session).Events.Count == 0, "Transport capture bypassed OFF mode.");
        normalizer.SetCapture(session, TraceCaptureMode.Metadata);
        await responses.StreamRawAsync(Sentinel, "synthetic-model", "{}");
        var sseEvents = normalizer.Read(session).Events;
        Check(sseEvents.Count == 4 && sseEvents[0].Direction == TraceDirection.Egress &&
              sseEvents[1].Status == 200 && sseEvents[^1].EventKind == TraceEventKind.Completed &&
              sseEvents.Select(e => e.RequestId).Distinct().Count() == 1, "SSE transport correlation/lifecycle failed.");

        var failed = false;
        using (var failureHttp = new HttpClient(new SyntheticHttpHandler(Sentinel, status: HttpStatusCode.BadRequest)))
        {
            try { await new ResponsesClient(failureHttp, binding).StreamRawAsync(Sentinel, "model", "{}"); }
            catch (InvalidOperationException) { failed = true; }
        }
        Check(failed && normalizer.Read(session).Events.Last().EventKind == TraceEventKind.Error, "HTTP failure was not traced.");
        using (var unfinishedHttp = new HttpClient(new SyntheticHttpHandler($"data: {Delta}\n\n")))
        {
            var incomplete = await new ResponsesClient(unfinishedHttp, binding).StreamRawAsync(Sentinel, "model", "{}");
            Check(!incomplete.Completed && normalizer.Read(session).Events.Last().CaptureGap, "Incomplete SSE was falsely complete.");
        }
        using (var cancelledHttp = new HttpClient(new SyntheticHttpHandler("", cancel: true)))
        {
            try { await new ResponsesClient(cancelledHttp, binding).StreamRawAsync(Sentinel, "model", "{}"); }
            catch (OperationCanceledException) { }
            Check(normalizer.Read(session).Events.Last().EventKind == TraceEventKind.Cancelled, "SSE cancellation was not traced.");
        }

        using var socket = new SyntheticWebSocket(Delta);
        await using (var websocket = new ResponsesWebSocketClient(socket, binding))
        {
            await websocket.SendRawAsync("""{"type":"response.create","input":"synthetic-transport-private-sentinel-51"}""", "model");
            var events = new List<string>();
            await foreach (var payload in websocket.ReadEventsAsync()) events.Add(payload);
            Check(events.SequenceEqual(new[] { Delta }) && socket.Sent is not null && socket.Sent.Contains(Sentinel),
                "WebSocket tracing changed fragmented UTF-8 or outgoing normalization.");
        }
        var wsEvents = normalizer.Read(session).Events.Where(e => e.ProviderSurface == TraceSurface.ResponsesWebSocket).ToArray();
        Check(wsEvents.Length == 3 && wsEvents[0].Direction == TraceDirection.Egress && wsEvents[1].Direction == TraceDirection.Ingress &&
              wsEvents[^1].EventKind == TraceEventKind.Completed, "WebSocket frame/closure trace failed.");

        // A real redirected stdio process exercises Codex framing; no Codex runtime or credentials.
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../.."));
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--synthetic-jsonrpc-fixture");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic stdio process unavailable.");
        await using (var codex = new CodexAppServerClient(process, binding))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await codex.InitializeAsync(deadline.Token);
            var result = await codex.RequestAsync("synthetic/echo", JsonSerializer.SerializeToElement(new { text = Sentinel }), deadline.Token);
            Check(result.GetProperty("echo").GetString() == Sentinel, "Tracing changed JSON-RPC response routing.");
        }
        var rpcEvents = normalizer.Read(session).Events.Where(e => e.ProviderSurface == TraceSurface.CodexJsonRpc).ToArray();
        foreach (var request in rpcEvents.Where(e => e.Direction == TraceDirection.Egress && e.EventKind == TraceEventKind.Request))
            Check(rpcEvents.Any(e => e.Direction == TraceDirection.Ingress && e.EventKind == TraceEventKind.Response && e.RequestId == request.RequestId),
                "JSON-RPC request/response alias correlation failed.");
        Check(rpcEvents.Count(e => e.Direction == TraceDirection.Egress && e.EventKind == TraceEventKind.Request) == 2,
            "Synthetic stdio did not exercise initialize and request boundaries.");
        var serialized = JsonSerializer.Serialize(normalizer.Read(session), TraceNormalizer.JsonOptions);
        Check(!serialized.Contains(Sentinel) && !serialized.Contains("synthetic/echo") && !serialized.Contains("response.output_text.delta"),
            "Transport trace leaked payloads, methods or raw event names.");
        Check(normalizer.Read(session).Events.All(e => e.Outcome is TraceOutcome.Observed or TraceOutcome.Requested), "Trace claimed verified provider actuation.");

        normalizer.Dispose();
        var resultWithBrokenRecorder = await responses.StreamRawAsync(Sentinel, "synthetic-model", "{}");
        Check(resultWithBrokenRecorder.Completed && binding.RecorderFailures > 0,
            "Recorder failure corrupted the provider operation or silently disappeared.");
        using var controller = new LocalTraceController();
        using var foreignController = new LocalTraceController();
        var foreignSession = foreignController.Execute("status").GetProperty("session_id").GetGuid();
        Check(controller.Execute("status").GetProperty("mode").GetString() == "OFF", "Controller must start OFF.");
        controller.Execute("start");
        controller.Binding.RecordJson(TraceSurface.ResponsesSse, TraceAdapter.Http, TraceDirection.Ingress, "synthetic", Delta);
        Check(controller.Execute("read").GetProperty("page").GetProperty("events").GetArrayLength() == 1,
            "Controller projection missed its bound transport.");
        foreach (var denied in new[]
        {
            JsonSerializer.SerializeToElement(new { session_id = foreignSession }),
            JsonSerializer.SerializeToElement(new { actor_principal = "forged" }),
            JsonSerializer.SerializeToElement(new { payload_mode = "privileged" })
        })
        {
            var refused = false;
            try { controller.Execute("read", denied); } catch (UnauthorizedAccessException) { refused = true; }
            Check(refused, "Controller accepted a foreign session or unsupported authority claim.");
        }
        controller.Execute("stop");
        controller.Binding.RecordJson(TraceSurface.ResponsesSse, TraceAdapter.Http, TraceDirection.Ingress, "synthetic", Delta);
        var exported = controller.Execute("export");
        Check(exported.GetProperty("mode").GetString() == "OFF" && exported.GetProperty("page").GetProperty("events").GetArrayLength() == 1 &&
            exported.GetProperty("capture_gap").GetBoolean() && !exported.GetRawText().Contains(Sentinel),
            "Controller stop/export changed capture scope, lost its gap or leaked payload.");
        Console.WriteLine("Owned transport synthetic checks PASS (HTTP SSE, fragmented WebSocket, persistent JSON-RPC)");
    }

    public static async Task RunRpcFixtureAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("id", out var id)) continue;
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result = new { echo = Sentinel } }));
            await Console.Out.FlushAsync();
        }
    }

    private sealed class SyntheticHttpHandler(string body, HttpStatusCode status = HttpStatusCode.OK, bool cancel = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancel) throw new OperationCanceledException(Sentinel);
            Check(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses", "Fixture request targeted wrong endpoint.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        }
    }

    private sealed class SyntheticWebSocket(string json) : WebSocket
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(json);
        private int _step;
        private WebSocketState _state = WebSocketState.Open;
        public string? Sent { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => "synthetic";
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        { Sent = Encoding.UTF8.GetString(buffer); return Task.CompletedTask; }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_step >= 2) { _state = WebSocketState.CloseReceived; return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)); }
            var split = Array.IndexOf(_bytes, (byte)0xe2) + 1; // Split inside the UTF-8 checkmark.
            var offset = _step == 0 ? 0 : split;
            var count = _step++ == 0 ? split : _bytes.Length - split;
            Array.Copy(_bytes, offset, buffer.Array!, buffer.Offset, count);
            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, _step == 2));
        }
    }
}
