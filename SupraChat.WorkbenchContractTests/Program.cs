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
        Check(cMd.Content.Contains("| Session | ID | Status | Worktree / branch | Conversation reference | Workspace reference | Terminal |", StringComparison.Ordinal), "markdown_table_header");
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

        // 6. LedgerDraft State Snapshot & Cross-Process Restoration Contract
        var snapAuthority = new ExperimentalLocalWorkbenchSessionAuthority();
        var s1 = snapAuthority.CreateAsync("snap-operation-001", "Snap Session 1").AsTask().GetAwaiter().GetResult();
        var s2 = snapAuthority.CreateAsync("snap-operation-002", "Snap Session 2").AsTask().GetAwaiter().GetResult();
        Check(s1.Applied && s2.Applied, "snap_sessions_created");

        var snapDrafts1 = new LedgerLocalDrafts(snapAuthority, () => clock);
        snapDrafts1.SelectAsync(s1.Session!.Id).AsTask().GetAwaiter().GetResult();
        var rev1 = snapDrafts1.PrepareAsync("chat-to-terminal", "STAGED_TEXT").AsTask().GetAwaiter().GetResult();
        var stage1 = snapDrafts1.StageReviewedAsync(rev1.ReviewId, "STAGED_TEXT").AsTask().GetAwaiter().GetResult();
        Check(stage1.Staged, "snap_draft_staged");
        var revPending = snapDrafts1.PrepareAsync("terminal-to-chat", "PENDING_TEXT").AsTask().GetAwaiter().GetResult();

        var exportedSnap = snapDrafts1.ExportSnapshot();
        Check(exportedSnap.SelectedId == s1.Session.Id, "exported_selected_id");
        Check(exportedSnap.PendingReview is not null && exportedSnap.PendingReview.ReviewId == revPending.ReviewId, "exported_pending_review");
        Check(exportedSnap.Buffers is not null && exportedSnap.Buffers.ContainsKey(s1.Session.Id), "exported_buffers_contain_s1");
        Check(exportedSnap.Buffers![s1.Session.Id].TerminalInput == "STAGED_TEXT", "exported_terminal_buffer");

        // Restore into an entirely independent LedgerLocalDrafts instance (simulating CLI process boundary)
        var snapDrafts2 = new LedgerLocalDrafts(snapAuthority, () => clock);
        snapDrafts2.RestoreSnapshot(exportedSnap);
        Check(snapDrafts2.SelectedId == s1.Session.Id, "restored_selected_id");
        Check(snapDrafts2.ReadSelected().TerminalInput == "STAGED_TEXT", "restored_terminal_buffer");
        // Exercise the restored pending review
        var stageRestored = snapDrafts2.StageReviewedAsync(revPending.ReviewId, "PENDING_TEXT").AsTask().GetAwaiter().GetResult();
        Check(stageRestored.Staged, "restored_pending_review_staged");
        Check(snapDrafts2.ReadSelected().ChatInput == "PENDING_TEXT", "restored_chat_buffer");
        // Verify one-time consumption: cannot restage
        MustRefuse(() => snapDrafts2.StageReviewedAsync(revPending.ReviewId, "PENDING_TEXT").AsTask().GetAwaiter().GetResult(), "restored_review_consumed");

        // Discard pending review
        var revToDiscard = snapDrafts2.PrepareAsync("chat-to-terminal", "WILL_DISCARD").AsTask().GetAwaiter().GetResult();
        snapDrafts2.DiscardPending();
        MustRefuse(() => snapDrafts2.StageReviewedAsync(revToDiscard.ReviewId, "WILL_DISCARD").AsTask().GetAwaiter().GetResult(), "discard_clears_pending");
        Check(snapDrafts2.ReadSelected().TerminalInput == "STAGED_TEXT", "discard_preserves_staged_buffer");

        // Forget session
        snapDrafts2.Forget(s1.Session.Id);
        MustRefuse(() => snapDrafts2.ReadSelected(), "forget_clears_session_buffers");

        // 7. Parallel-session Indicators, Worktree Metadata, Collisions, Lifecycle & 16-Session Capacity
        var hardenAuth = new ExperimentalLocalWorkbenchSessionAuthority();
        var h1 = hardenAuth.CreateAsync("harden-op-0001", "Hardened 1").AsTask().GetAwaiter().GetResult();
        Check(h1.Applied && h1.Session is not null, "harden_created");

        // Status transitions
        var stRun = hardenAuth.SetStatusAsync("harden-op-0002", h1.Session!.Id, h1.Session.Version, "running").AsTask().GetAwaiter().GetResult();
        Check(stRun.Applied && stRun.Session!.SessionState == "running", "status_running");
        var stWait = hardenAuth.SetStatusAsync("harden-op-0003", h1.Session.Id, stRun.Session!.Version, "waiting").AsTask().GetAwaiter().GetResult();
        Check(stWait.Applied && stWait.Session!.SessionState == "waiting", "status_waiting");
        var stErr = hardenAuth.SetStatusAsync("harden-op-0004", h1.Session.Id, stWait.Session!.Version, "error").AsTask().GetAwaiter().GetResult();
        Check(stErr.Applied && stErr.Session!.SessionState == "error", "status_error");
        var stIdle = hardenAuth.SetStatusAsync("harden-op-0005", h1.Session.Id, stErr.Session!.Version, "idle").AsTask().GetAwaiter().GetResult();
        Check(stIdle.Applied && stIdle.Session!.SessionState == "idle", "status_idle");

        // Unread flag & display badge
        var stUnread = hardenAuth.SetUnreadAsync("harden-op-0006", h1.Session.Id, stIdle.Session!.Version, true).AsTask().GetAwaiter().GetResult();
        Check(stUnread.Applied && stUnread.Session!.HasUnread, "has_unread_true");
        var projUnread = hardenAuth.ReadAsync(h1.Session.Id).AsTask().GetAwaiter().GetResult();
        Check(projUnread.Snapshot!.Sessions[0].DisplayBadge == "[idle] •", "display_badge_unread");

        MustRefuse(() => hardenAuth.SetStatusAsync("harden-op-0007", h1.Session.Id, stUnread.Session!.Version, "bogus_status").AsTask().GetAwaiter().GetResult(), "invalid_status_refused");

        // Worktree branch detection
        Check(WorkspaceMetadataDetector.DetectBranch(null) is null, "detect_null");
        Check(WorkspaceMetadataDetector.DetectBranch("nonexistent_path_xyz") is null, "detect_nonexistent");
        var tempGitDir = Path.Combine(Path.GetTempPath(), "suprachat_wt_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempGitDir, ".git"));
            File.WriteAllText(Path.Combine(tempGitDir, ".git", "HEAD"), "ref: refs/heads/feature/worktree-test\n");
            var detected = WorkspaceMetadataDetector.DetectBranch(tempGitDir);
            Check(detected == "feature/worktree-test", "detect_standard_git");

            var tempWorktreeDir = Path.Combine(Path.GetTempPath(), "suprachat_wt2_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempWorktreeDir);
                var wtGitDir = Path.Combine(tempGitDir, ".git", "worktrees", "sub-worktree");
                Directory.CreateDirectory(wtGitDir);
                File.WriteAllText(Path.Combine(wtGitDir, "HEAD"), "ref: refs/heads/wt-linked-branch\n");
                File.WriteAllText(Path.Combine(tempWorktreeDir, ".git"), $"gitdir: {wtGitDir}\n");
                var detectedLinked = WorkspaceMetadataDetector.DetectBranch(tempWorktreeDir);
                Check(detectedLinked == "wt-linked-branch", "detect_worktree_pointer");
            }
            finally
            {
                if (Directory.Exists(tempWorktreeDir)) Directory.Delete(tempWorktreeDir, true);
            }
        }
        finally
        {
            if (Directory.Exists(tempGitDir)) Directory.Delete(tempGitDir, true);
        }

        // Collision warnings with details
        var colConv = "https://chatgpt.com/c/11111111-2222-3333-4444-555555555555";
        var colWs = "colliding/workspace/path";
        var hCol1 = hardenAuth.CreateAsync("harden-op-0010", "Col 1", colConv, colWs).AsTask().GetAwaiter().GetResult();
        var hCol2 = hardenAuth.CreateAsync("harden-op-0011", "Col 2", colConv, colWs).AsTask().GetAwaiter().GetResult();
        Check(hCol1.Applied && hCol2.Applied, "collision_sessions_created");
        var receiptCol = hardenAuth.ReadAsync().AsTask().GetAwaiter().GetResult();
        Check(receiptCol.Snapshot!.Collisions.Count >= 2, "two_collisions_detected");
        var convCol = receiptCol.Snapshot.Collisions.FirstOrDefault(c => c.Type == "DUPLICATE_CONVERSATION");
        Check(convCol is not null && convCol.Detail == colConv, "collision_detail_conversation");
        var wsCol = receiptCol.Snapshot.Collisions.FirstOrDefault(c => c.Type == "SHARED_WORKSPACE");
        Check(wsCol is not null && wsCol.Detail == colWs, "collision_detail_workspace");

        // Export preview contains collision details & new columns
        var mdExport = WorkbenchMetadataExport.Prepare(receiptCol.Snapshot, "markdown");
        Check(mdExport.Content.Contains(colConv, StringComparison.Ordinal), "markdown_export_has_col_detail");
        Check(mdExport.Content.Contains("| Status | Worktree / branch |", StringComparison.Ordinal), "markdown_export_has_columns");
        var jsonExport = WorkbenchMetadataExport.Prepare(receiptCol.Snapshot, "json");
        Check(jsonExport.Content.Contains("\"detail\":", StringComparison.Ordinal), "json_export_has_collision_detail");
        Check(jsonExport.Content.Contains("\"session_state\":", StringComparison.Ordinal), "json_export_has_session_state");

        // Close / Archive and Restore lifecycle
        var beforeCloseSnap = hardenAuth.ReadAsync().AsTask().GetAwaiter().GetResult();
        var activeCountBefore = beforeCloseSnap.Snapshot!.Sessions.Count;
        var closeRes = hardenAuth.CloseAsync("harden-op-0020", hCol1.Session!.Id, hCol1.Session.Version).AsTask().GetAwaiter().GetResult();
        Check(closeRes.Applied && closeRes.Session!.IsArchived, "session_archived");
        var afterCloseSnap = hardenAuth.ReadAsync().AsTask().GetAwaiter().GetResult();
        Check(afterCloseSnap.Snapshot!.Sessions.Count == activeCountBefore - 1, "active_count_decremented");
        Check(afterCloseSnap.Snapshot.ArchivedSessions is not null &&
              afterCloseSnap.Snapshot.ArchivedSessions.Any(s => s.Id == hCol1.Session.Id), "session_in_archived_list");

        // Restore
        var restoreRes = hardenAuth.RestoreAsync("harden-op-0021", hCol1.Session.Id, closeRes.Session!.Version).AsTask().GetAwaiter().GetResult();
        Check(restoreRes.Applied && !restoreRes.Session!.IsArchived, "session_restored");
        var afterRestoreSnap = hardenAuth.ReadAsync().AsTask().GetAwaiter().GetResult();
        Check(afterRestoreSnap.Snapshot!.Sessions.Count == activeCountBefore, "active_count_restored");

        // 16-session capacity performance qualification
        var capBench = new SessionWorkbench();
        for (var i = 0; i < SessionWorkbench.MaximumSessions; i++)
        {
            capBench.Open($"Session {i + 1}");
        }
        var fullSnap = capBench.Snapshot();
        Check(fullSnap.Sessions.Count == SessionWorkbench.MaximumSessions, "capacity_16_sessions_filled");
        MustRefuse(() => capBench.Open("Overflow"), "capacity_17th_session_refused");

        // Close 1 session -> 15 active, 1 archived
        var closedId = fullSnap.Sessions[0].Id;
        Check(capBench.Close(closedId), "close_one_of_16");
        var after1Close = capBench.Snapshot();
        Check(after1Close.Sessions.Count == 15, "15_active_sessions");
        Check(after1Close.ArchivedSessions!.Count == 1, "1_archived_session");

        // Can open another session to reach 16 again
        var reOpened = capBench.Open("Re-opened slot");
        Check(capBench.Snapshot().Sessions.Count == SessionWorkbench.MaximumSessions, "restored_to_16_active");
        MustRefuse(() => capBench.Open("Overflow again"), "overflow_again_refused");

        // Restore when at capacity should fail
        MustRefuse(() => capBench.Restore(closedId), "restore_at_full_capacity_refused");

        Console.WriteLine($"WORKBENCH_SOURCE_ONLY_CONTRACT_PASS cases={_cases} provider_contact=false terminal_execution=false browser_submission=false persistent_state=false");
        return 0;
    }
}
