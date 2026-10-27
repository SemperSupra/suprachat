using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SupraChat.Core;

internal static class DurableTransportCancellationChecks
{
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Durable transport cancellation failed."); }
    internal static async Task RunAsync()
    {
        SessionLedgerCheckpoint? saved = null;
        var ledger = new SharedSessionLedger(persist: cp => saved = JsonSerializer.Deserialize<SessionLedgerCheckpoint>(JsonSerializer.Serialize(cp))!);
        var session = ledger.Open("durable-transport-open", "automation", "synthetic-transports").Session!.Id;
        using var controls = new LocalOwnedStreamControls(Guid.ParseExact(session, "N"));
        var grant = Guid.NewGuid().ToString("N");
        CancellationAdmission Admit() => new(new(true, "synthetic-transport-actor", "automation", session, grant), true);
        void Cancel(TraceSurface surface, string key)
        {
            var state = controls.List().Single(s => s.Surface == surface);
            var version = ledger.InspectAll().Sessions.Single().Version;
            var target = ledger.RegisterCancellationTarget(session, controls, state.StreamId);
            var plan = ledger.PreviewCancellation(session, target, version, state.Revision, key, Admit).Entry!.Plan;
            var receipt = ledger.ConfirmCancellation(session, plan.PlanId, target, version, state.Revision, key, true, Admit);
            Require(receipt.Status == "VERIFIED_LOCAL_SIGNAL" && !receipt.ProviderStopVerified && !receipt.CallbackCompletionVerified);
            Require(saved!.CancellationJournal!.Last().Stages.Last().Stage == "VERIFIED_LOCAL_SIGNAL");
        }
        using (var http = new HttpClient(new GovernedControlChecks.BlockingHttpHandler()))
        {
            var response = new ResponsesClient(http, null, controls).StreamTextAsync("synthetic", "synthetic", "synthetic");
            Cancel(TraceSurface.ResponsesSse, "durable-transport-http");
            var cancelled = false; try { await response.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && controls.List().Count == 0);
        }
        foreach (var closing in new[] { false, true })
        {
            using var socket = new GovernedControlChecks.BlockingSocket(closing);
            await using var client = new ResponsesWebSocketClient(socket, controls: controls);
            var read = Task.Run(async () => { await foreach (var _ in client.ReadEventsAsync()) { } });
            if (closing) await socket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Cancel(TraceSurface.ResponsesWebSocket, closing ? "durable-transport-close" : "durable-transport-websocket");
            var cancelled = false; try { await read.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled);
        }
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../.."));
        var start = new ProcessStartInfo(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--synthetic-jsonrpc-control-fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var codex = new CodexAppServerClient(Process.Start(start)!, controls: controls))
        {
            await codex.InitializeAsync(deadline.Token); await using var events = codex.SubscribeEvents();
            await codex.RequestAsync("turn/start", JsonSerializer.SerializeToElement(new { threadId = "synthetic-thread" }), deadline.Token);
            Cancel(TraceSurface.CodexJsonRpc, "durable-transport-codex");
            var completed = await events.Reader.ReadAsync(deadline.Token);
            Require(completed.Method == "turn/completed" && completed.Payload.GetProperty("turn").GetProperty("status").GetString() == "interrupted");
            Require(controls.List().Count == 0);
        }
        Console.WriteLine("Durable transport checks PASS (synthetic HTTP/WS/blocked close/Codex interruption; no remote provider verification)");
    }
}
