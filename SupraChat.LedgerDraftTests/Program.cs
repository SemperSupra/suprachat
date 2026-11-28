using SupraChat.Core;

static class Program
{
    static int passed;
    static void Check(bool yes, string label)
    {
        if (!yes) throw new InvalidOperationException("LEDGER_DRAFT_TEST_FAILED_" + label);
        passed++;
    }

    static void Refuse(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { passed++; return; }
        catch (InvalidOperationException) { passed++; return; }
        throw new InvalidOperationException("LEDGER_DRAFT_REFUSAL_MISSING_" + label);
    }

    static async Task RefuseAsync(Func<Task> action, string label)
    {
        try { await action(); }
        catch (ArgumentException) { passed++; return; }
        catch (InvalidOperationException) { passed++; return; }
        throw new InvalidOperationException("LEDGER_DRAFT_ASYNC_REFUSAL_MISSING_" + label);
    }

    static async Task<int> Main()
    {
        // 1. Synchronous SharedSessionLedger test suite
        var ledger = new SharedSessionLedger();
        var a = ledger.Open("draft-open-key-001", "human", "Erste Sitzung");
        var b = ledger.Open("draft-open-key-002", "human", "ห้องสนทนา");
        Check(a.Applied && b.Applied && a.Session is not null && b.Session is not null,
            "two_authoritative_ledger_ids");
        var baseline = ledger.InspectAll().Sequence;
        var clock = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        using var drafts = new LedgerLocalDrafts(ledger, () => clock);
        Refuse(() => drafts.Prepare("chat-to-terminal", "synthetic"), "selection_required");
        Refuse(() => drafts.Select("fake-session"), "unknown_session");
        drafts.Select(a.Session!.Id);
        Check(drafts.SelectedId == a.Session.Id, "selected_ledger_id");

        var review = drafts.Prepare("chat-to-terminal", "ONLY_SYNTHETIC_DRAFT");
        Check(review.Schema == LedgerLocalDrafts.Schema && !review.Applied &&
            !review.ChatSubmitted && !review.CommandExecuted, "review_no_effect");
        Refuse(() => drafts.StageReviewed(review.ReviewId, "CHANGED"), "changed_input_refused");
        Refuse(() => drafts.StageReviewed(review.ReviewId, "ONLY_SYNTHETIC_DRAFT"),
            "changed_review_is_consumed");
        review = drafts.Prepare("chat-to-terminal", "ONLY_SYNTHETIC_DRAFT");
        var stage = drafts.StageReviewed(review.ReviewId, "ONLY_SYNTHETIC_DRAFT");
        Check(stage.Staged && !stage.CommandExecuted && !stage.ChatSubmitted &&
            !stage.RemoteRequestCreated && stage.Destination == "LOCAL_DRAFT_ONLY", "stage_never_executes");
        Check(drafts.ReadSelected().TerminalInput == "ONLY_SYNTHETIC_DRAFT", "local_buffer");
        Refuse(() => drafts.StageReviewed(review.ReviewId, "ONLY_SYNTHETIC_DRAFT"), "one_use");

        drafts.Select(b.Session!.Id);
        Check(drafts.ReadSelected().TerminalInput.Length == 0 &&
              drafts.ReadSelected().ChatInput.Length == 0, "session_isolation");
        var secondReview = drafts.Prepare("terminal-to-chat", "UNSUBMITTED_ONLY");
        drafts.Select(a.Session.Id);
        Refuse(() => drafts.StageReviewed(secondReview.ReviewId, "UNSUBMITTED_ONLY"),
            "cross_selection_invalidation");
        Check(drafts.ReadSelected().TerminalInput == "ONLY_SYNTHETIC_DRAFT",
            "prior_draft_not_overwritten");
        drafts.Select(b.Session.Id);
        secondReview = drafts.Prepare("terminal-to-chat", "UNSUBMITTED_ONLY");
        Check(drafts.StageReviewed(secondReview.ReviewId, "UNSUBMITTED_ONLY").Staged,
            "second_direction_staged_only");
        Check(drafts.ReadSelected().ChatInput == "UNSUBMITTED_ONLY", "chat_buffer_only");
        drafts.Select(a.Session.Id);

        var stale = drafts.Prepare("chat-to-terminal", "OLD_VERSION_REVIEW");
        var change = ledger.Change("draft-change-key-01", "human", a.Session.Id,
            a.Session.Version, "set-title", "Renamed");
        Check(change.Applied, "authoritative_ledger_change");
        Refuse(() => drafts.StageReviewed(stale.ReviewId, "OLD_VERSION_REVIEW"),
            "ledger_version_fence");
        Check(drafts.ReadSelected().TerminalInput == "ONLY_SYNTHETIC_DRAFT",
            "version_refusal_keeps_old_content");

        var expiring = drafts.Prepare("chat-to-terminal", "EXPIRE_ME");
        clock = clock.AddMinutes(3);
        Refuse(() => drafts.StageReviewed(expiring.ReviewId, "EXPIRE_ME"), "expired");
        var future = drafts.Prepare("chat-to-terminal", "CLOCK_TEST");
        clock = clock.AddMinutes(-4);
        Refuse(() => drafts.StageReviewed(future.ReviewId, "CLOCK_TEST"),
            "backward_clock");
        Refuse(() => drafts.Prepare("invalid-direction", "TEST"), "direction");
        Refuse(() => drafts.Prepare("chat-to-terminal", new string('界', 6000)),
            "utf8_byte_limit");
        Refuse(() => drafts.Prepare("chat-to-terminal", "text\0bad"), "nul_refusal");

        Check(ledger.InspectAll().Sequence == baseline + 1,
            "no_new_ledger_mutation_from_local_staging");
        drafts.Forget(a.Session.Id);
        Refuse(() => drafts.ReadSelected(), "forget_clears_selection");
        drafts.Select(b.Session.Id);
        Check(drafts.ReadSelected().ChatInput == "UNSUBMITTED_ONLY",
            "forget_does_not_erase_other_session");
        drafts.Dispose();
        Refuse(() => drafts.ReadSelected(), "disposed_refusal");

        // 2. Asynchronous IWorkbenchSessionAuthority test suite
        IWorkbenchSessionAuthority authority = new ExperimentalLocalWorkbenchSessionAuthority();
        var c = await authority.CreateAsync("auth-operation-001", "Authority Session 1");
        var d = await authority.CreateAsync("auth-operation-002", "Authority Session 2");
        Check(c.Applied && d.Applied && c.Session is not null && d.Session is not null,
            "authority_sessions_created");

        using var asyncDrafts = new LedgerLocalDrafts(authority, () => clock);
        await RefuseAsync(async () => await asyncDrafts.PrepareAsync("chat-to-terminal", "async text"), "async_selection_required");
        await RefuseAsync(async () => await asyncDrafts.SelectAsync("nonexistent-id"), "async_unknown_session");
        await asyncDrafts.SelectAsync(c.Session!.Id);
        Check(asyncDrafts.SelectedId == c.Session.Id, "async_selected_ledger_id");

        var asyncReview = await asyncDrafts.PrepareAsync("chat-to-terminal", "ASYNC_SYNTHETIC_DRAFT");
        Check(asyncReview.Schema == LedgerLocalDrafts.Schema && !asyncReview.Applied, "async_review_prepared");
        await RefuseAsync(async () => await asyncDrafts.StageReviewedAsync(asyncReview.ReviewId, "TAMPERED"), "async_tampered_refused");
        await RefuseAsync(async () => await asyncDrafts.StageReviewedAsync(asyncReview.ReviewId, "ASYNC_SYNTHETIC_DRAFT"), "async_consumed_refused");

        asyncReview = await asyncDrafts.PrepareAsync("chat-to-terminal", "ASYNC_SYNTHETIC_DRAFT");
        var asyncStage = await asyncDrafts.StageReviewedAsync(asyncReview.ReviewId, "ASYNC_SYNTHETIC_DRAFT");
        Check(asyncStage.Staged && asyncStage.Destination == "LOCAL_DRAFT_ONLY", "async_staged");
        Check(asyncDrafts.ReadSelected().TerminalInput == "ASYNC_SYNTHETIC_DRAFT", "async_buffer_read");

        // Async version fence check
        var asyncReview2 = await asyncDrafts.PrepareAsync("chat-to-terminal", "BEFORE_RENAME");
        var renameResult = await authority.RenameAsync("rename-operation-001", c.Session.Id, c.Session.Version, "Renamed Authority Session");
        Check(renameResult.Applied, "authority_session_renamed");
        await RefuseAsync(async () => await asyncDrafts.StageReviewedAsync(asyncReview2.ReviewId, "BEFORE_RENAME"), "async_version_fence");

        await CloseReconciliationAsync();
        await DelayedDraftsAsync();
        Console.WriteLine($"LEDGER_DRAFT_LOCAL_ONLY_PASS cases={passed} " +
            "provider_contact=false terminal_execution=false persistent_state=false " +
            "actor_authentication=false payloads_logged=false");
        return 0;
    }

    private static async Task CloseReconciliationAsync()
    {
        var authority = new DelayedCloseAuthority();
        var closer = new WorkbenchSessionClose(authority);
        var a = (await authority.CreateAsync("close-fixture-a-001", "A")).Session!;
        var b = (await authority.CreateAsync("close-fixture-b-001", "B")).Session!;
        var selected = a.Id;
        bool ASelected() => selected == a.Id;
        Check((await closer.ExecuteAsync(a.Id, ASelected, retryOnly: true)).Status == "NO_PENDING_CLOSE" &&
            authority.Requests.Count == 0, "close_reconcile_without_pending_never_dispatches");
        var first = closer.ExecuteAsync(a.Id, ASelected).AsTask();
        Check(authority.Requests.Count == 1 && !first.IsCompleted, "close_dispatch_waits");
        Check((await closer.ExecuteAsync(a.Id, ASelected)).Status == "CLOSE_PENDING",
            "close_duplicate_in_flight_refused");
        selected = b.Id;
        await authority.CompleteAsync(loseAcknowledgment: true);
        Check((await first).Status == "OUTCOME_UNKNOWN", "close_lost_ack_unknown");
        Check(closer.PendingSessions().SequenceEqual([a.Id]), "close_lost_ack_remains_discoverable");
        Check((await authority.InspectAsync(b.Id))?.Version == b.Version, "close_unrelated_b_untouched");
        var archived = (await authority.InspectAsync(a.Id))!;
        Check(archived.IsArchived && archived.Version == a.Version + 1, "close_committed_once");

        selected = a.Id;
        var replay = closer.ExecuteAsync(a.Id, ASelected, retryOnly: true).AsTask();
        Check(authority.Requests[0] == authority.Requests[1], "close_retry_exact_request");
        await authority.CompleteAsync();
        var replayed = await replay;
        Check(replayed.Status == "ARCHIVED" && !replayed.MayDiscardDrafts,
            "historical_replay_cannot_discard_drafts");

        var restored = (await authority.RestoreAsync("close-restore-a-001", a.Id, archived.Version)).Session!;
        var closing = closer.ExecuteAsync(a.Id, ASelected).AsTask();
        await authority.CompleteAsync(beforeAcknowledgment: async receipt =>
        {
            await authority.RestoreAsync("close-restore-a-002", a.Id, receipt.Session!.Version);
        });
        var superseded = await closing;
        Check(superseded.Status == "ARCHIVE_SUPERSEDED" && !superseded.MayDiscardDrafts,
            "late_close_after_restore_keeps_drafts");
        Check(authority.Requests[2].Version == restored.Version, "new_close_binds_current_revision");

        var other = closer.ExecuteAsync(a.Id, ASelected).AsTask();
        selected = b.Id;
        await authority.CompleteAsync();
        Check(!(await other).MayDiscardDrafts, "late_close_after_switch_keeps_drafts");
        var closeB = closer.ExecuteAsync(b.Id, () => selected == b.Id).AsTask();
        await authority.CompleteAsync();
        Check((await closeB).MayDiscardDrafts, "confirmed_current_close_can_discard_prior_drafts");
        archived = (await authority.InspectAsync(a.Id))!;
        var explicitRestore = await authority.RestoreAsync("close-restore-a-003", a.Id, archived.Version);
        Check(explicitRestore.Applied && explicitRestore.Session is { IsArchived: false } reopened && reopened.Id == a.Id,
            "explicit_restore_targets_a");
        Check((await authority.InspectAsync(b.Id)) is { IsArchived: true }, "explicit_restore_leaves_b_archived");
        var conflict = closer.ExecuteAsync(a.Id, () => true).AsTask();
        await authority.RenameAsync("close-rename-a-001", a.Id, explicitRestore.Session!.Version, "A renamed");
        await authority.CompleteAsync();
        var refused = await conflict;
        Check(refused.Status == "VERSION_CONFLICT" && !refused.MayDiscardDrafts && closer.PendingSessions().Length == 0,
            "known_close_refusal_does_not_discard_or_require_replay");
    }

    private static async Task DelayedDraftsAsync()
    {
        var authority = new DelayedCloseAuthority();
        var a = (await authority.CreateAsync("draft-delay-a-001", "A")).Session!;
        var b = (await authority.CreateAsync("draft-delay-b-001", "B")).Session!;
        using var drafts = new LedgerLocalDrafts(authority);
        var delayed = authority.DelayNextInspection();
        var selectA = drafts.SelectAsync(a.Id).AsTask();
        await drafts.SelectAsync(b.Id);
        delayed.SetResult(a);
        await selectA;
        Check(drafts.SelectedId == b.Id, "late_selection_cannot_replace_newer_selection");

        await drafts.SelectAsync(a.Id);
        var reviewA = await drafts.PrepareAsync("chat-to-terminal", "SYNTHETIC_A");
        delayed = authority.DelayNextInspection();
        var stageA = drafts.StageReviewedAsync(reviewA.ReviewId, reviewA.PreviewText).AsTask();
        await drafts.SelectAsync(b.Id);
        var reviewB = await drafts.PrepareAsync("chat-to-terminal", "SYNTHETIC_B");
        delayed.SetResult(a);
        await RefuseAsync(async () => await stageA, "late_stage_refused_after_selection_change");
        await drafts.StageReviewedAsync(reviewB.ReviewId, reviewB.PreviewText);
        Check(drafts.ReadSelected().TerminalInput == "SYNTHETIC_B", "newer_target_review_preserved");
        await drafts.SelectAsync(a.Id);
        Check(drafts.ReadSelected().TerminalInput.Length == 0, "late_stage_cannot_recreate_old_buffer");
    }

    private sealed class DelayedCloseAuthority : IWorkbenchSessionAuthority
    {
        private readonly ExperimentalLocalWorkbenchSessionAuthority _owner = new();
        private TaskCompletionSource<SessionLedgerResult>? _pending;
        private TaskCompletionSource<SessionLedgerState?>? _inspection;
        public List<(string Key, string Id, long Version)> Requests { get; } = [];

        public ValueTask<SessionLedgerResult> CloseAsync(string operationId, string sessionId,
            long expectedVersion, CancellationToken token = default)
        {
            Requests.Add((operationId, sessionId, expectedVersion));
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(_pending.Task);
        }

        public async Task CompleteAsync(bool loseAcknowledgment = false,
            Func<SessionLedgerResult, Task>? beforeAcknowledgment = null)
        {
            var pending = _pending!;
            var (key, id, version) = Requests[^1];
            var receipt = await _owner.CloseAsync(key, id, version);
            if (beforeAcknowledgment is not null)
                await beforeAcknowledgment(receipt);
            if (loseAcknowledgment)
                pending.SetException(new TimeoutException());
            else
                pending.SetResult(receipt);
        }

        public TaskCompletionSource<SessionLedgerState?> DelayNextInspection() =>
            _inspection = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionLedgerState?> InspectAsync(string sessionId, CancellationToken token = default)
        {
            var delayed = _inspection;
            _inspection = null;
            return delayed is null ? _owner.InspectAsync(sessionId, token) : new(delayed.Task);
        }

        public ValueTask<SessionLedgerResult> CreateAsync(string operationId, string title,
            string? conversationUrl = null, string? workspaceKey = null, CancellationToken token = default) =>
            _owner.CreateAsync(operationId, title, conversationUrl, workspaceKey, token);

        public ValueTask<SessionLedgerResult> RestoreAsync(string operationId, string sessionId,
            long expectedVersion, CancellationToken token = default) =>
            _owner.RestoreAsync(operationId, sessionId, expectedVersion, token);

        public ValueTask<LedgerWorkbenchReadReceipt> ReadAsync(string? selectedSessionId = null,
            CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<SessionLedgerResult> RenameAsync(string operationId, string sessionId,
            long expectedVersion, string title, CancellationToken token = default) =>
            _owner.RenameAsync(operationId, sessionId, expectedVersion, title, token);

        public ValueTask<SessionLedgerResult> SetStatusAsync(string operationId, string sessionId,
            long expectedVersion, string status, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<SessionLedgerResult> SetUnreadAsync(string operationId, string sessionId,
            long expectedVersion, bool hasUnread, CancellationToken token = default) => throw new NotSupportedException();
    }
}
