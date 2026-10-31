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

    static int Main()
    {
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
        Console.WriteLine($"LEDGER_DRAFT_LOCAL_ONLY_PASS cases={passed} " +
            "provider_contact=false terminal_execution=false persistent_state=false " +
            "actor_authentication=false payloads_logged=false");
        return 0;
    }
}
