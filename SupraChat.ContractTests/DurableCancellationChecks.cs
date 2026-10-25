using System.Text.Json;
using SupraChat.Core;

internal static class DurableCancellationChecks
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Durable cancellation check failed."); }
    private static SessionLedgerCheckpoint Clone(SessionLedgerCheckpoint checkpoint) =>
        JsonSerializer.Deserialize<SessionLedgerCheckpoint>(JsonSerializer.Serialize(checkpoint))!;
    private sealed class Fixture : IAsyncDisposable
    {
        public SessionLedgerCheckpoint? Saved;
        public readonly Clock Clock = new();
        public readonly SharedSessionLedger Ledger;
        public readonly string Session;
        public readonly LocalOwnedStreamControls Owner;
        public readonly LocalOwnedStreamControls.OwnedStreamLease Stream;
        public readonly Guid Target;
        public readonly string Grant = Guid.NewGuid().ToString("N");
        public Func<SessionLedgerCheckpoint, bool>? FailBefore;
        public Func<SessionLedgerCheckpoint, bool>? FailAfter;
        public Fixture()
        {
            Ledger = new(persist: checkpoint =>
            {
                if (FailBefore?.Invoke(checkpoint) == true) throw new IOException("synthetic-before-commit");
                Saved = Clone(checkpoint);
                if (FailAfter?.Invoke(checkpoint) == true) throw new IOException("synthetic-after-commit");
            }, controlClock: Clock);
            Session = Ledger.Open("durable-cancel-open", "automation", "synthetic-cancellation").Session!.Id;
            Owner = new(Guid.ParseExact(Session, "N")); Stream = Owner.Register(TraceSurface.Synthetic);
            Target = Ledger.RegisterCancellationTarget(Session, Owner, Stream);
        }
        public CancellationAdmission Admit() => new(new(true, "synthetic-actor", "automation", Session, Grant), true);
        public DurableCancellationResult Preview(string key = "synthetic-cancel-key") => Ledger.PreviewCancellation(Session, Target, 0, 0, key, Admit);
        public DurableCancellationResult Confirm(DurableCancellationPlan plan, bool confirmed = true) =>
            Ledger.ConfirmCancellation(Session, plan.PlanId, Target, 0, 0, plan.ActionKey, confirmed, Admit);
        public async ValueTask DisposeAsync() { await Stream.DisposeAsync(); Owner.Dispose(); }
    }
    public static async Task RunAsync()
    {
        await using (var f = new Fixture())
        {
            var plan = f.Preview().Entry!.Plan;
            Require(!f.Stream.Token.IsCancellationRequested && f.Saved!.CancellationJournal!.Single().Confirmed is null);
            Require(f.Saved!.Schema == SharedSessionLedger.ControlCheckpointSchema);
            var downgraded = f.Saved with { Schema = SharedSessionLedger.Schema };
            var schemaRefused = false;
            try { _ = new SharedSessionLedger(downgraded, _ => { }); } catch (InvalidDataException) { schemaRefused = true; }
            Require(schemaRefused);
            schemaRefused = false;
            try { _ = new SharedSessionLedger(f.Saved with { CancellationJournal = null }, _ => { }); }
            catch (InvalidDataException) { schemaRefused = true; }
            Require(schemaRefused);
            var before = f.Saved;
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => f.Confirm(plan))));
            Require(results.All(r => r.Status == "VERIFIED_LOCAL_SIGNAL" && !r.ProviderStopVerified && !r.CallbackCompletionVerified));
            Require(f.Stream.Revision == 1 && f.Stream.Token.IsCancellationRequested && results.Count(r => !r.IsReplay) == 1);
            Require(results[0].Entry!.Stages.Select(s => s.Stage).SequenceEqual(new[] { "REQUESTED", "APPLIED_LOCAL_SIGNAL", "VERIFIED_LOCAL_SIGNAL" }));
            var recovered = new SharedSessionLedger(Clone(f.Saved!), _ => { });
            var replay = recovered.ConfirmCancellation(f.Session, plan.PlanId, f.Target, 0, 0, plan.ActionKey, true, f.Admit);
            Require(replay.IsReplay && replay.Status == "VERIFIED_LOCAL_SIGNAL");
            Require(f.Confirm(plan, false).Status == "IDEMPOTENCY_CONFLICT");
            var renewed = f.Admit() with { Actor = f.Admit().Actor with { GrantId = Guid.NewGuid().ToString("N") } };
            Require(f.Ledger.ConfirmCancellation(f.Session, plan.PlanId, f.Target, 0, 0, plan.ActionKey, true, () => renewed).Status == "VERIFIED_LOCAL_SIGNAL");
            // Returned arrays cannot rewrite immutable journal history.
            results[0].Entry!.Stages[0] = new(0, "tampered", TraceOutcome.Verified, true);
            Require(f.Confirm(plan).Entry!.Stages[0].Stage == "REQUESTED");
            var priorHost = new SharedSessionLedger(Clone(before!), _ => { });
            Require(priorHost.ConfirmCancellation(f.Session, plan.PlanId, f.Target, 0, 0, plan.ActionKey, true, f.Admit).Status == "HOST_RUN_CHANGED");
        }
        foreach (var fault in new[] { "BEFORE_REQUEST", "AFTER_REQUEST", "BEFORE_APPLIED", "AFTER_APPLIED" })
        {
            await using var f = new Fixture(); var plan = f.Preview().Entry!.Plan;
            bool Match(SessionLedgerCheckpoint cp, string stage) => cp.CancellationJournal!.Single().Stages.LastOrDefault()?.Stage == stage;
            if (fault == "BEFORE_REQUEST") f.FailBefore = cp => Match(cp, "REQUESTED");
            if (fault == "AFTER_REQUEST") f.FailAfter = cp => Match(cp, "REQUESTED");
            if (fault == "BEFORE_APPLIED") f.FailBefore = cp => Match(cp, "APPLIED_LOCAL_SIGNAL");
            if (fault == "AFTER_APPLIED") f.FailAfter = cp => Match(cp, "APPLIED_LOCAL_SIGNAL");
            var failed = false; try { f.Confirm(plan); } catch (IOException) { failed = true; }
            Require(failed && f.Stream.Token.IsCancellationRequested == (fault is "BEFORE_APPLIED" or "AFTER_APPLIED"));
            var recovered = new SharedSessionLedger(Clone(f.Saved!), _ => { });
            var result = recovered.ConfirmCancellation(f.Session, plan.PlanId, f.Target, 0, 0, plan.ActionKey, true, f.Admit);
            Require(result.Status == (fault == "BEFORE_REQUEST" ? "HOST_RUN_CHANGED" : fault == "AFTER_APPLIED" ? "APPLIED_LOCAL_SIGNAL" : "OUTCOME_UNKNOWN_FRESH_APPROVAL_REQUIRED"));
            Require(!result.ProviderStopVerified && !result.CallbackCompletionVerified);
        }
        await using (var f = new Fixture())
        {
            // An exclusive durable claim also invalidates plans created through the legacy API beforehand.
            await using var extra = f.Owner.Register(TraceSurface.Synthetic);
            var legacyKey = Guid.NewGuid();
            var legacy = f.Owner.Preview(Guid.ParseExact(f.Session, "N"), extra.StreamId, OwnedControlAction.Cancel, 0, legacyKey);
            f.Ledger.RegisterCancellationTarget(f.Session, f.Owner, extra);
            Require(f.Owner.Apply(Guid.ParseExact(f.Session, "N"), legacy.PlanId, 0, legacyKey, true).Reason == "DURABLE_CONTROL_REQUIRED");
            Require(f.Owner.Preview(Guid.ParseExact(f.Session, "N"), extra.StreamId, OwnedControlAction.Cancel, 0, Guid.NewGuid()).Reason == "DURABLE_CONTROL_REQUIRED");
            var another = new SharedSessionLedger(Clone(f.Saved!), _ => { });
            var refused = false; try { another.RegisterCancellationTarget(f.Session, f.Owner, extra); } catch (UnauthorizedAccessException) { refused = true; }
            Require(refused && !extra.Token.IsCancellationRequested);
            var plan = f.Preview().Entry!.Plan;
            var renewed = f.Admit() with { Actor = f.Admit().Actor with { GrantId = Guid.NewGuid().ToString("N") } };
            Require(f.Ledger.ConfirmCancellation(f.Session, plan.PlanId, f.Target, 0, 0, plan.ActionKey, true, () => renewed).Status == "GRANT_CHANGED_FRESH_REVIEW_REQUIRED");
            // Disposed targets do not consume lifetime capacity.
            for (var i = 0; i < 80; i++)
            {
                await using var target = f.Owner.Register(TraceSurface.Synthetic);
                f.Ledger.RegisterCancellationTarget(f.Session, f.Owner, target);
            }
            for (var i = 1; i < 128; i++) Require(f.Preview("synthetic-capacity-" + i).Status == "CONFIRMATION_REQUIRED");
            Require(f.Preview("synthetic-capacity-overflow").Status == "CONTROL_CAPACITY");
        }
        foreach (var refusal in new[] { "NO_SCOPE", "WRONG_ACTOR", "WRONG_SESSION", "REVOKED", "EXPIRED", "STALE", "REMOVED", "UNCONFIRMED" })
        {
            await using var f = new Fixture(); var plan = f.Preview().Entry!.Plan;
            Func<CancellationAdmission> admit = f.Admit;
            if (refusal == "NO_SCOPE") admit = () => f.Admit() with { CancellationAllowed = false };
            if (refusal == "WRONG_ACTOR") admit = () => f.Admit() with { Actor = f.Admit().Actor with { ActorId = "another-actor" } };
            if (refusal == "WRONG_SESSION") admit = () => f.Admit() with { Actor = f.Admit().Actor with { SessionId = Guid.NewGuid().ToString("N") } };
            if (refusal == "REVOKED") f.Ledger.RevokeOwnGrant(f.Grant, f.Session, "automation");
            if (refusal == "EXPIRED") f.Clock.Seconds = 121;
            if (refusal == "STALE") f.Ledger.Change("durable-cancel-stale", "automation", f.Session, 0, "set-title", "synthetic-changed");
            if (refusal == "REMOVED") await f.Stream.DisposeAsync();
            var result = f.Ledger.ConfirmCancellation(f.Session, plan.PlanId, f.Target, 0, 0, plan.ActionKey, refusal != "UNCONFIRMED", admit);
            Require(result.Status != "VERIFIED_LOCAL_SIGNAL" && f.Stream.Revision == 0);
        }
        await using (var f = new Fixture())
        {
            var plan = f.Preview().Entry!.Plan;
            using var callback = f.Stream.Token.Register(() => throw new InvalidOperationException("synthetic-private-callback"));
            Require(f.Confirm(plan).Status == "VERIFIED_LOCAL_SIGNAL");
            // Valid stage types cannot be reordered to manufacture verification on restore.
            var cp = Clone(f.Saved!); var journal = cp.CancellationJournal![0];
            cp.CancellationJournal[0] = journal with { Stages = new[] { journal.Stages.Last() } };
            var refused = false; try { _ = new SharedSessionLedger(cp, _ => { }); } catch (InvalidDataException) { refused = true; }
            Require(refused);
        }
        // Legacy snapshots load with no control records or implicit enrolled targets.
        var old = new SessionLedgerCheckpoint(SharedSessionLedger.Schema, 0, Array.Empty<SessionLedgerState>(), Array.Empty<SessionLedgerCheckpointOperation>(), Array.Empty<SessionLedgerEvent>());
        _ = new SharedSessionLedger(JsonSerializer.Deserialize<SessionLedgerCheckpoint>(JsonSerializer.Serialize(old))!, _ => { });
        Console.WriteLine("Durable cancellation checks PASS (synthetic write-ahead, replay, crash windows, CAS, admission, callback failure)");
    }
}
