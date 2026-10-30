using SupraChat.Core;

static class Program
{
    private static int _cases;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("WORKBENCH_CASE_FAILED_" + name);
        _cases++;
    }
    private static void MustRefuse(Action action, string name)
    {
        try { action(); }
        catch (ArgumentException) { _cases++; return; }
        catch (InvalidOperationException) { _cases++; return; }
        throw new InvalidOperationException("WORKBENCH_REFUSAL_MISSING_" + name);
    }
    static int Main()
    {
        // No local desktop, browser, credential, network, terminal process or profile.
        var id = Guid.NewGuid();
        var inspected = ChatGptControlContract.Inspect($"https://chatgpt.com/c/{id:D}");
        Check(inspected.Recognized && inspected.ConversationId == id.ToString("D"), "recognized_reference");
        Check(inspected.AdapterState == "NOT_CONNECTED", "no_adapter_authority");
        Check(!ChatGptControlContract.Inspect($"https://chatgpt.com/c/{id:D}?token=x").Recognized, "query_refused");
        Check(!ChatGptControlContract.Inspect($"https://elsewhere.example/c/{id:D}").Recognized, "foreign_origin_refused");
        Check(!ChatGptControlContract.Inspect($"http://chatgpt.com/c/{id:D}").Recognized, "insecure_origin_refused");
        Check(!ChatGptControlContract.Inspect("https://chatgpt.com/").Recognized, "home_is_not_conversation");

        var w = new SessionWorkbench();
        var first = w.Open("First", $"https://chatgpt.com/c/{id:D}", "Test/Workspace");
        var second = w.Open("Second", workspaceKey: "test\\workspace");
        var snap = w.Snapshot();
        Check(snap.Sessions.Count == 2 && snap.ActiveSessionId == second.Id, "two_local_sessions");
        Check(snap.Collisions.Count == 1 && snap.Collisions[0].Type == "SHARED_WORKSPACE", "workspace_collision");
        Check(!snap.TerminalExecutionPermitted && !snap.ChatSubmissionPermitted, "no_capability_escalation");
        Check(w.Select(first.Id), "select");
        var preview = w.PreviewTransfer(first.Id, "chat-to-terminal", "SYNTHETIC_ONLY_DRAFT");
        Check(preview.RequiresExplicitCommit && !preview.WasApplied &&
              !preview.CommandExecutionPermitted && preview.DestinationMode == "INSERT_ONLY_NOT_EXECUTE",
              "preview_only");
        MustRefuse(() => w.PreviewTransfer(first.Id, "other", "SYNTHETIC_ONLY_DRAFT"), "invalid_direction");
        MustRefuse(() => w.PreviewTransfer(first.Id, "chat-to-terminal", new string('x', 16385)), "input_bound");
        MustRefuse(() => w.Open(conversationUrl: "https://attacker.invalid"), "unrecognized_conversation");

        var clock = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        var drafts = new WorkbenchLocalDrafts(w, () => clock);
        var r = drafts.Prepare(first.Id, "chat-to-terminal", "SYNTHETIC_ONLY_DRAFT");
        Check(r.RequiresExplicitUserAction && !r.TerminalExecutionPermitted && !r.ChatSubmissionPermitted,
            "review_no_execution");
        var done = drafts.StageReviewed(first.Id, r.ReviewId, "SYNTHETIC_ONLY_DRAFT");
        Check(done.DraftStaged && !done.CommandExecuted && !done.ChatSubmitted && !done.RemoteRequestCreated,
            "stage_buffer_only");
        Check(drafts.Read(first.Id).TerminalInputDraft == "SYNTHETIC_ONLY_DRAFT", "isolated_local_buffer");
        Check(drafts.Read(second.Id).TerminalInputDraft.Length == 0, "other_session_empty");
        MustRefuse(() => drafts.StageReviewed(first.Id, r.ReviewId, "SYNTHETIC_ONLY_DRAFT"), "one_shot");
        r = drafts.Prepare(first.Id, "terminal-to-chat", "SECOND_SYNTHETIC_ONLY");
        MustRefuse(() => drafts.StageReviewed(first.Id, r.ReviewId, "CHANGED"), "changed_review");
        MustRefuse(() => drafts.StageReviewed(first.Id, r.ReviewId, "SECOND_SYNTHETIC_ONLY"), "changed_revoked");
        r = drafts.Prepare(first.Id, "terminal-to-chat", "EXPIRING_SYNTHETIC_ONLY");
        clock = clock.AddMinutes(3);
        MustRefuse(() => drafts.StageReviewed(first.Id, r.ReviewId, "EXPIRING_SYNTHETIC_ONLY"), "expired_review");

        var js = WorkbenchMetadataExport.Prepare(w.Snapshot(), "json");
        var md = WorkbenchMetadataExport.Prepare(w.Snapshot(), "markdown");
        Check(js.Content.Contains("SESSION_METADATA_ONLY", StringComparison.Ordinal), "metadata_scope");
        Check(js.Content.Contains("\"exported_credentials\": false", StringComparison.Ordinal) &&
              js.Content.Contains("\"exported_transcripts\": false", StringComparison.Ordinal),
              "no_sensitive_exports");
        Check(!js.Content.Contains("SYNTHETIC_ONLY_DRAFT", StringComparison.Ordinal) &&
              !md.Content.Contains("SYNTHETIC_ONLY_DRAFT", StringComparison.Ordinal),
              "no_draft_payload_exported");
        Check(!js.WasSaved && js.RequiresUserSelectedDestination &&
              !md.WasSaved && md.RequiresUserSelectedDestination, "no_implicit_file_write");
        MustRefuse(() => WorkbenchMetadataExport.Prepare(w.Snapshot(), "pdf"), "unimplemented_pdf_refused");

        Check(w.Move(first.Id, 1) && w.Close(first.Id), "move_and_close");
        drafts.ForgetSession(first.Id);
        MustRefuse(() => drafts.Read(first.Id), "closed_session_isolation");
        for (var n = 0; n < 15; n++) w.Open();
        Check(w.Snapshot().Sessions.Count == SessionWorkbench.MaximumSessions, "capacity");
        MustRefuse(() => w.Open(), "capacity_refusal");
        Console.WriteLine($"WORKBENCH_SOURCE_ONLY_CONTRACT_PASS cases={_cases} provider_contact=false terminal_execution=false browser_submission=false persistent_state=false");
        return 0;
    }
}
