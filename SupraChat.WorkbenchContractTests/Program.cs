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
    private static void MustBeDisposed(Action action, string name)
    {
        try { action(); }
        catch (ObjectDisposedException) { _cases++; return; }
        throw new InvalidOperationException("WORKBENCH_DISPOSED_REFUSAL_MISSING_" + name);
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
        using var drafts = new WorkbenchLocalDrafts(w, () => clock);
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

        r = drafts.Prepare(first.Id, "chat-to-terminal", "SELECTION_CHANGE_SYNTHETIC");
        Check(w.Select(second.Id) && w.Select(first.Id), "selection_round_trip");
        MustRefuse(() => drafts.StageReviewed(first.Id, r.ReviewId, "SELECTION_CHANGE_SYNTHETIC"),
            "selection_change_revokes_review");
        Check(drafts.PendingReviewCount == 0, "selection_change_clears_review_payload");

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

        Check(drafts.RetainedSessionCount == 1, "one_live_session_draft_retained");
        Check(w.Move(first.Id, 1) && w.Close(first.Id), "move_and_close");
        Check(drafts.RetainedSessionCount == 0, "close_clears_session_draft_payload");
        MustRefuse(() => drafts.Read(first.Id), "closed_session_isolation");

        // Repeated close/open cycles never retain draft text outside the live-session bound.
        for (var n = 0; n < 64; n++)
        {
            var cycle = w.Open("Cycle " + n);
            var cycleReview = drafts.Prepare(cycle.Id, "chat-to-terminal", "CYCLE_SYNTHETIC_ONLY");
            Check(drafts.StageReviewed(cycle.Id, cycleReview.ReviewId, "CYCLE_SYNTHETIC_ONLY").DraftStaged,
                "cycle_draft_staged");
            Check(drafts.RetainedSessionCount == 1, "cycle_retained_only_while_live");
            Check(w.Close(cycle.Id), "cycle_session_closed");
            Check(drafts.RetainedSessionCount == 0, "cycle_close_releases_payload");
        }

        var disposedWorkbench = new SessionWorkbench();
        var disposedSession = disposedWorkbench.Open("Dispose fixture");
        var disposedDrafts = new WorkbenchLocalDrafts(disposedWorkbench, () => clock);
        var disposedReview = disposedDrafts.Prepare(disposedSession.Id, "chat-to-terminal", "DISPOSED_SYNTHETIC_ONLY");
        disposedDrafts.Dispose();
        Check(disposedDrafts.PendingReviewCount == 0 && disposedDrafts.RetainedSessionCount == 0,
            "dispose_clears_payloads");
        MustBeDisposed(() => disposedDrafts.Prepare(disposedSession.Id, "chat-to-terminal", "AFTER_DISPOSE"),
            "prepare_after_dispose");
        MustBeDisposed(() => disposedDrafts.StageReviewed(disposedSession.Id, disposedReview.ReviewId,
            "DISPOSED_SYNTHETIC_ONLY"), "stage_after_dispose");
        MustBeDisposed(() => disposedDrafts.Read(disposedSession.Id), "read_after_dispose");
        for (var n = 0; n < 15; n++) w.Open();
        Check(w.Snapshot().Sessions.Count == SessionWorkbench.MaximumSessions, "capacity");
        MustRefuse(() => w.Open(), "capacity_refusal");

        // Category 1 Interactive Surface: Collision Warnings (DUPLICATE_CONVERSATION and SHARED_WORKSPACE)
        var collisionWorkbench = new SessionWorkbench();
        var sharedUrl = $"https://chatgpt.com/c/{Guid.NewGuid():D}";
        var c1 = collisionWorkbench.Open("Session Alpha", sharedUrl, "project/frontend");
        var c2 = collisionWorkbench.Open("Session Beta", sharedUrl, "PROJECT\\FRONTEND");
        var collisionSnap = collisionWorkbench.Snapshot();
        Check(collisionSnap.Collisions.Count == 2, "two_collisions_detected");
        Check(collisionSnap.Collisions.Any(c => c.Type == "DUPLICATE_CONVERSATION" && c.LeftSessionId == c1.Id && c.RightSessionId == c2.Id), "duplicate_conversation_collision");
        Check(collisionSnap.Collisions.Any(c => c.Type == "SHARED_WORKSPACE" && c.LeftSessionId == c1.Id && c.RightSessionId == c2.Id), "shared_workspace_collision");
        var cMd = WorkbenchMetadataExport.Prepare(collisionSnap, "markdown");
        Check(cMd.Content.Contains("- DUPLICATE_CONVERSATION:", StringComparison.Ordinal), "markdown_duplicate_conversation_warning");
        Check(cMd.Content.Contains("- SHARED_WORKSPACE:", StringComparison.Ordinal), "markdown_shared_workspace_warning");
        Check(cMd.Content.Contains("| Session | ID | Conversation reference | Workspace reference | Terminal |", StringComparison.Ordinal), "markdown_table_header");
        Check(cMd.Content.Contains("Active session: " + c2.Id, StringComparison.Ordinal), "markdown_active_session");
        Check(cMd.Content.Contains("Chat submission: disabled. Terminal execution: disabled.", StringComparison.Ordinal), "markdown_disabled_capabilities");
        Check(cMd.Content.Contains("Excluded: credentials, transcripts, terminal output, and transfer preview text.", StringComparison.Ordinal), "markdown_excluded_notice");
        Check(collisionWorkbench.Close(c2.Id), "close_conflicting_session");
        var resolvedSnap = collisionWorkbench.Snapshot();
        Check(resolvedSnap.Collisions.Count == 0, "collisions_cleared_on_close");
        var resolvedMd = WorkbenchMetadataExport.Prepare(resolvedSnap, "markdown");
        Check(resolvedMd.Content.Contains("None observed.", StringComparison.Ordinal), "markdown_none_observed_when_cleared");

        // Category 1 Interactive Surface: CAS VERSION_CONFLICT Status Readout & Recovery
        var casAuthority = new ExperimentalLocalWorkbenchSessionAuthority();
        var createOutcome = casAuthority.CreateAsync("cas-operation-001", "Initial Title", sharedUrl, "test/workspace").AsTask().GetAwaiter().GetResult();
        Check(createOutcome.Applied && createOutcome.Session is not null, "cas_session_created");
        var casSessionId = createOutcome.Session!.Id;
        Check(createOutcome.Session.Version == 0, "initial_version_zero");
        // Concurrent update bumps version from 0 to 1
        var concurrentOutcome = casAuthority.RenameAsync("cas-concurrent-op-001", casSessionId, 0, "Concurrent Title").AsTask().GetAwaiter().GetResult();
        Check(concurrentOutcome.Applied && concurrentOutcome.Session!.Version == 1, "concurrent_update_applied");
        // Stale rename with expected version 0 produces VERSION_CONFLICT
        var staleRename = casAuthority.RenameAsync("cas-stale-operation", casSessionId, 0, "Stale Title").AsTask().GetAwaiter().GetResult();
        Check(!staleRename.Applied && staleRename.Status == "VERSION_CONFLICT", "cas_version_conflict_readout");
        // Recovery: inspect to read current state/version, then retry with updated version
        var inspectedCurrent = casAuthority.InspectAsync(casSessionId).AsTask().GetAwaiter().GetResult();
        Check(inspectedCurrent is not null && inspectedCurrent.Version == 1 && inspectedCurrent.Title == "Concurrent Title", "cas_recovery_inspect");
        var recoveredRename = casAuthority.RenameAsync("cas-recovery-operation", casSessionId, inspectedCurrent!.Version, "Recovered Title").AsTask().GetAwaiter().GetResult();
        Check(recoveredRename.Applied && recoveredRename.Session!.Version == 2 && recoveredRename.Session.Title == "Recovered Title", "cas_version_conflict_recovery");

        // Category 1 Interactive Surface: Accessibility Preference Normalization (ScaleUp, ScaleDown, ScaleReset, ReducedMotion)
        var defA11y = AccessibilityPreferences.Default;
        Check(defA11y.InterfaceScale == 1.0 && !defA11y.ReducedMotion, "a11y_default");
        var clampedLow = AccessibilityPreferencesStore.Normalize(new(AccessibilityPreferencesStore.Schema, 0.4, false));
        Check(clampedLow.InterfaceScale == AccessibilityPreferencesStore.MinimumScale, "a11y_clamp_min_0_8");
        var clampedHigh = AccessibilityPreferencesStore.Normalize(new(AccessibilityPreferencesStore.Schema, 3.5, true));
        Check(clampedHigh.InterfaceScale == AccessibilityPreferencesStore.MaximumScale && clampedHigh.ReducedMotion, "a11y_clamp_max_2_0");
        var steppedDown = AccessibilityPreferencesStore.Normalize(new(AccessibilityPreferencesStore.Schema, 1.0 - AccessibilityPreferencesStore.ScaleStep, false));
        Check(Math.Abs(steppedDown.InterfaceScale - 0.9) < 0.001, "a11y_scale_down_step");
        var steppedUp = AccessibilityPreferencesStore.Normalize(new(AccessibilityPreferencesStore.Schema, 1.0 + AccessibilityPreferencesStore.ScaleStep, false));
        Check(Math.Abs(steppedUp.InterfaceScale - 1.1) < 0.001, "a11y_scale_up_step");
        var steppedReset = AccessibilityPreferencesStore.Normalize(new(AccessibilityPreferencesStore.Schema, 1.0, true));
        Check(steppedReset.InterfaceScale == 1.0 && steppedReset.ReducedMotion, "a11y_scale_reset_step");
        var rounded = AccessibilityPreferencesStore.Normalize(new(AccessibilityPreferencesStore.Schema, 1.24, false));
        Check(Math.Abs(rounded.InterfaceScale - 1.2) < 0.001, "a11y_round_to_nearest_tenth");

        // Multi-Provider Authentication & Order Configuration Contract Tests
        // 1. AuthModes Normalization
        Check(AuthModes.NormalizeMode("subscription") == AuthModes.Subscription, "auth_mode_sub");
        Check(AuthModes.NormalizeMode("chatgpt") == AuthModes.Subscription, "auth_mode_chatgpt");
        Check(AuthModes.NormalizeMode("sub") == AuthModes.Subscription, "auth_mode_sub_alias");
        Check(AuthModes.NormalizeMode("siwc") == AuthModes.Siwc, "auth_mode_siwc");
        Check(AuthModes.NormalizeMode("chatgpt_plan") == AuthModes.Siwc, "auth_mode_chatgpt_plan");
        Check(AuthModes.NormalizeMode("plan") == AuthModes.Siwc, "auth_mode_plan");
        Check(AuthModes.NormalizeMode("api_key") == AuthModes.ApiKey, "auth_mode_api_key");
        Check(AuthModes.NormalizeMode("apikey") == AuthModes.ApiKey, "auth_mode_apikey_alias");
        Check(AuthModes.NormalizeMode("key") == AuthModes.ApiKey, "auth_mode_key_alias");
        MustRefuse(() => AuthModes.NormalizeMode("unsupported_auth_mode"), "auth_mode_unknown_refused");

        // 2. AuthPolicy Normalization & Order Preservation
        var defOrder = AuthPolicy.NormalizeOrder(null);
        Check(defOrder.Length == 3 && defOrder[0] == AuthModes.Subscription && defOrder[1] == AuthModes.Siwc && defOrder[2] == AuthModes.ApiKey, "auth_order_default");
        var emptyOrder = AuthPolicy.NormalizeOrder(Array.Empty<string>());
        Check(emptyOrder.Length == 3 && emptyOrder[0] == AuthModes.Subscription, "auth_order_empty_fallback");
        var customOrder = AuthPolicy.NormalizeOrder(new[] { "api-key", "subscription" });
        Check(customOrder.Length == 2 && customOrder[0] == AuthModes.ApiKey && customOrder[1] == AuthModes.Subscription, "auth_order_custom");
        var dedupOrder = AuthPolicy.NormalizeOrder(new[] { "chatgpt", "api_key", "sub", "siwc", "api-key" });
        Check(dedupOrder.Length == 3 && dedupOrder[0] == AuthModes.Subscription && dedupOrder[1] == AuthModes.ApiKey && dedupOrder[2] == AuthModes.Siwc, "auth_order_dedup");

        // 3. SubscriptionCredential Display & Expiry
        var subWithEmail = new SubscriptionCredential("tok-1", Email: "test@example.com");
        Check(subWithEmail.DisplayLabel.Contains("test@example.com"), "sub_display_email");
        var subWithAccount = new SubscriptionCredential("tok-2", AccountId: "acc-12345");
        Check(subWithAccount.DisplayLabel.Contains("acc-12345"), "sub_display_account");
        var subFallback = new SubscriptionCredential("tok-3");
        Check(subFallback.DisplayLabel == "ChatGPT subscription", "sub_display_fallback");
        var expiredSub = new SubscriptionCredential("tok-4", ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        Check(expiredSub.IsExpired, "sub_is_expired");
        var activeSub = new SubscriptionCredential("tok-5", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
        Check(!activeSub.IsExpired, "sub_is_active");

        // 4. ApiKeyCredential Redaction
        var normalKey = new ApiKeyCredential("sk-1234567890abcdef");
        Check(normalKey.RedactedKey == "sk-1...cdef", "api_key_redacted_normal");
        var shortKey = new ApiKeyCredential("sk-1234");
        Check(shortKey.RedactedKey == "sk-...", "api_key_redacted_short");
        var emptyKey = new ApiKeyCredential("");
        Check(emptyKey.RedactedKey == "(empty)", "api_key_redacted_empty");

        // 5. AuthResolution Evaluation Contract
        var dummyPolicy = new AuthPolicy(new[] { AuthModes.ApiKey });
        var report = AuthResolver.EvaluateAsync(dummyPolicy, requirePlanUsage: true).GetAwaiter().GetResult();
        Check(report.ConfiguredOrder.Length == 1 && report.ConfiguredOrder[0] == AuthModes.ApiKey, "auth_eval_configured_order");
        Check(report.Evaluations.Count == 1 && report.Evaluations[0].AuthMode == AuthModes.ApiKey, "auth_eval_evaluations_count");

        Console.WriteLine($"WORKBENCH_SOURCE_ONLY_CONTRACT_PASS cases={_cases} provider_contact=false terminal_execution=false browser_submission=false persistent_state=false");
        return 0;
    }
}
