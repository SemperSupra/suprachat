using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SupraChat.Core;

internal static class GovernedControlChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var session = Guid.NewGuid();
        var clock = new ManualClock();
        using var controls = new LocalOwnedStreamControls(session, clock);
        await using var first = controls.Register(TraceSurface.ResponsesSse);
        await using var second = controls.Register(TraceSurface.ResponsesWebSocket);
        var key = Guid.NewGuid();
        var plan = controls.Preview(session, first.StreamId, OwnedControlAction.Cancel, 0, key);
        Check(plan.Outcome == TraceOutcome.Requested && !first.Token.IsCancellationRequested, "Preview applied an effect.");
        var foreignRefused = false;
        try { controls.Apply(Guid.NewGuid(), plan.PlanId, 0, key, true); } catch (UnauthorizedAccessException) { foreignRefused = true; }
        Check(foreignRefused, "Foreign control session accepted.");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => controls.Apply(session, plan.PlanId, 0, key, true))));
        Check(attempts.All(r => r == attempts[0]) && first.Token.IsCancellationRequested && !second.Token.IsCancellationRequested &&
            attempts[0].Revision == 1 && attempts[0].LocalCancellationVerified && !attempts[0].ProviderStopVerified,
            "Cancellation replay, session isolation or receipt evidence failed.");
        Check(controls.Apply(session, plan.PlanId, 1, key, true).Reason == "IDEMPOTENCY_CONFLICT", "Conflicting key reuse accepted.");
        var unknownPlanId = Guid.NewGuid();
        var unknownKey = Guid.NewGuid();
        var firstRefusal = controls.Apply(session, unknownPlanId, 0, unknownKey, true);
        var replacementPlan = controls.Preview(session, second.StreamId, OwnedControlAction.Cancel, 0, unknownKey);
        Check(controls.Apply(session, replacementPlan.PlanId, 0, unknownKey, true).Reason == "IDEMPOTENCY_CONFLICT" &&
            controls.Apply(session, unknownPlanId, 0, unknownKey, true) == firstRefusal && !second.Token.IsCancellationRequested,
            "Unknown-plan refusal was mutable across action-key reuse.");
        foreach (var action in new[] { OwnedControlAction.ModifyFirstParty, OwnedControlAction.CapturePayload, (OwnedControlAction)999 })
            Check(controls.Preview(session, second.StreamId, action, 0, Guid.NewGuid()).Outcome == TraceOutcome.Refused,
                "Unavailable live actuation admitted.");
        Check(controls.Preview(session, second.StreamId, OwnedControlAction.SyntheticReplay, 0, Guid.NewGuid()).Reason == "SYNTHETIC_ONLY",
            "Synthetic replay targeted a provider connection.");
        var unconfirmed = controls.Preview(session, second.StreamId, OwnedControlAction.Cancel, 0, Guid.NewGuid());
        var denied = controls.Apply(session, unconfirmed.PlanId, 0, unconfirmed.ActionKey, false);
        Check(denied.Outcome == TraceOutcome.Refused && !second.Token.IsCancellationRequested &&
            controls.Apply(session, unconfirmed.PlanId, 0, unconfirmed.ActionKey, false) == denied,
            "Unconfirmed action or immutable refusal failed.");
        var expired = controls.Preview(session, second.StreamId, OwnedControlAction.Cancel, 0, Guid.NewGuid());
        clock.Advance(TimeSpan.FromMinutes(3));
        Check(controls.Apply(session, expired.PlanId, 0, expired.ActionKey, true).Reason == "PLAN_EXPIRED", "Expired approval accepted.");

        await using var synthetic = controls.Register(TraceSurface.Synthetic);
        var drop = controls.Preview(session, synthetic.StreamId, OwnedControlAction.SyntheticDrop, 0, Guid.NewGuid());
        var stale = controls.Preview(session, synthetic.StreamId, OwnedControlAction.SyntheticReplay, 0, Guid.NewGuid());
        controls.Apply(session, drop.PlanId, 0, drop.ActionKey, true);
        Check(controls.Apply(session, stale.PlanId, 0, stale.ActionKey, true).Reason == "STALE_REVISION", "CAS conflict applied an effect.");
        var replay = controls.Preview(session, synthetic.StreamId, OwnedControlAction.SyntheticReplay, 1, Guid.NewGuid());
        var replayResult = controls.Apply(session, replay.PlanId, 1, replay.ActionKey, true);
        Check(controls.Apply(session, replay.PlanId, 1, replay.ActionKey, true) == replayResult, "Synthetic action replay changed first outcome.");
        var delay = controls.Preview(session, synthetic.StreamId, OwnedControlAction.SyntheticDelay, 2, Guid.NewGuid());
        controls.Apply(session, delay.PlanId, 2, delay.ActionKey, true);
        Check((await synthetic.ReadSyntheticFixtureAsync()).SequenceEqual(new[] { "synthetic-frame-1", "synthetic-frame-2", "synthetic-frame-3", "synthetic-frame-3" }),
            "Synthetic delay/drop/replay fixture effects failed.");
        var fault = controls.Preview(session, synthetic.StreamId, OwnedControlAction.SyntheticFault, 3, Guid.NewGuid());
        controls.Apply(session, fault.PlanId, 3, fault.ActionKey, true);
        var faulted = false;
        try { await synthetic.ReadSyntheticFixtureAsync(); } catch (InvalidOperationException) { faulted = true; }
        Check(faulted, "Synthetic fault fixture did not fail.");

        using var controller = new LocalTraceController();
        controller.Execute("start");
        var ownerSession = controller.Execute("status").GetProperty("session_id").GetGuid();
        using var blockedHttp = new HttpClient(new BlockingHttpHandler());
        var responseTask = new ResponsesClient(blockedHttp, controller.Binding, controller.Controls).StreamTextAsync("synthetic", "synthetic", "synthetic");
        var target = controller.Controls.List().Single();
        var traceAlias = controller.Execute("streams").GetProperty("streams")[0].GetProperty("trace_stream_id").GetString();
        Check(controller.Execute("read").GetProperty("page").GetProperty("events").EnumerateArray().Any(e =>
            e.GetProperty("stream_id").GetString() == traceAlias), "Control target did not correlate with trace metadata.");
        var cancel = controller.Controls.Preview(ownerSession, target.StreamId, OwnedControlAction.Cancel, target.Revision, Guid.NewGuid());
        controller.Controls.Apply(ownerSession, cancel.PlanId, target.Revision, cancel.ActionKey, true);
        var cancelled = false;
        try { await responseTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && controller.Controls.List().Count == 0, "Governed cancellation did not reach HTTP transport or release target.");

        using var socket = new BlockingSocket();
        await using (var websocket = new ResponsesWebSocketClient(socket, controller.Binding, controller.Controls))
        {
            var read = Task.Run(async () => { await foreach (var _ in websocket.ReadEventsAsync()) { } });
            var wsTarget = controller.Controls.List().Single();
            var wsPlan = controller.Controls.Preview(ownerSession, wsTarget.StreamId, OwnedControlAction.Cancel, 0, Guid.NewGuid());
            controller.Controls.Apply(ownerSession, wsPlan.PlanId, 0, wsPlan.ActionKey, true);
            cancelled = false;
            try { await read.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Governed cancellation did not reach WebSocket receive.");
        }
        using var closingSocket = new BlockingSocket(blockClose: true);
        await using (var closingClient = new ResponsesWebSocketClient(closingSocket, controller.Binding, controller.Controls))
        {
            var read = Task.Run(async () => { await foreach (var _ in closingClient.ReadEventsAsync()) { } });
            await closingSocket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closingTarget = controller.Controls.List().Single();
            var closingPlan = controller.Controls.Preview(ownerSession, closingTarget.StreamId, OwnedControlAction.Cancel, 0, Guid.NewGuid());
            controller.Controls.Apply(ownerSession, closingPlan.PlanId, 0, closingPlan.ActionKey, true);
            cancelled = false;
            try { await read.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Governed cancellation could not interrupt a blocked WebSocket close acknowledgment.");
        }

        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../.."));
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
          CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--synthetic-jsonrpc-control-fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var codex = new CodexAppServerClient(Process.Start(start)!, controller.Binding, controller.Controls))
        {
            await codex.InitializeAsync(deadline.Token);
            await using var events = codex.SubscribeEvents();
            await codex.RequestAsync("turn/start", JsonSerializer.SerializeToElement(new { threadId = "synthetic-thread" }), deadline.Token);
            var codexTarget = controller.Controls.List().Single();
            var codexPlan = controller.Controls.Preview(ownerSession, codexTarget.StreamId, OwnedControlAction.Cancel, 0, Guid.NewGuid());
            controller.Controls.Apply(ownerSession, codexPlan.PlanId, 0, codexPlan.ActionKey, true);
            var completed = await events.Reader.ReadAsync(deadline.Token);
            Check(completed.Method == "turn/completed" && completed.Payload.GetProperty("turn").GetProperty("status").GetString() == "interrupted",
                "Governed Codex cancellation did not issue supported turn/interrupt.");
            Check(controller.Controls.List().Count == 0, "Completed Codex turn left a control target behind.");
        }
        start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
        start.ArgumentList.Add("--synthetic-jsonrpc-control-exit-fixture");
        await using (var exiting = new CodexAppServerClient(Process.Start(start)!, controller.Binding, controller.Controls))
        {
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await exiting.InitializeAsync(exitDeadline.Token);
            await exiting.RequestAsync("turn/start", JsonSerializer.SerializeToElement(new { threadId = "synthetic-thread" }), exitDeadline.Token);
            while (controller.Controls.List().Count != 0 && !exitDeadline.IsCancellationRequested) await Task.Delay(20, exitDeadline.Token);
            Check(controller.Controls.List().Count == 0, "Abrupt Codex stdout termination leaked an active control target.");
        }
        await using var wireTarget = controller.Controls.Register(TraceSurface.Synthetic);
        var wirePlan = controller.Execute("control/preview", JsonSerializer.SerializeToElement(new
        { stream_id = wireTarget.StreamId, action = "synthetic_replay", expected_revision = 0, action_key = Guid.NewGuid() }));
        var wireApply = JsonSerializer.SerializeToElement(new
        {
            plan_id = wirePlan.GetProperty("plan_id").GetGuid(), action_key = wirePlan.GetProperty("action_key").GetGuid(),
            expected_revision = 0, confirmed = true
        });
        var wireResult = controller.Execute("control/apply", wireApply);
        Check(wireResult.GetProperty("outcome").GetString() == "APPLIED" &&
            wireResult.GetRawText() == controller.Execute("control/apply", wireApply).GetRawText() &&
            (await wireTarget.ReadSyntheticFixtureAsync()).Count == 5, "Common wire projection did not preserve approved first outcome.");
        using var bounded = new LocalOwnedStreamControls(Guid.NewGuid());
        var leases = Enumerable.Range(0, 16).Select(_ => bounded.Register(TraceSurface.Synthetic)).ToArray();
        var capacityRefused = false;
        try { bounded.Register(TraceSurface.Synthetic); } catch (InvalidOperationException) { capacityRefused = true; }
        Check(capacityRefused, "Active control targets exceeded their resource bound.");
        foreach (var lease in leases) await lease.DisposeAsync();
        Console.WriteLine("Governed control checks PASS (approval, CAS, immutable replay, synthetic effects, HTTP/WS/Codex cancellation)");
    }

    public static async Task RunRpcFixtureAsync(bool abrupt = false)
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;
            var method = root.GetProperty("method").GetString();
            object result = method == "turn/start" ? new { turn = new { id = "synthetic-turn" } } : new { };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.Clone(), result }));
            if (abrupt && method == "turn/start") { await Console.Out.FlushAsync(); return; }
            if (method == "turn/interrupt")
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                { method = "turn/completed", @params = new { threadId = "synthetic-thread", turn = new { id = "synthetic-turn", status = "interrupted" } } }));
            await Console.Out.FlushAsync();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    internal sealed class BlockingHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return new HttpResponseMessage(HttpStatusCode.OK); }
    }
    internal sealed class BlockingSocket(bool blockClose = false) : WebSocket
    {
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken)
        { CloseStarted.TrySetResult(); if (blockClose) await Task.Delay(Timeout.Infinite, cancellationToken); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (blockClose) { _state = WebSocketState.CloseReceived; return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true); }
            await Task.Delay(Timeout.Infinite, cancellationToken); return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
    }
}
