using SupraChat.Core;

internal static class SharedSessionAdmissionChecks
{
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Shared admission contract failed."); }

    public static async Task RunAsync()
    {
        SessionLedgerCheckpoint? saved = null;
        var ledger = new SharedSessionLedger(persist: checkpoint => saved = checkpoint);
        var a = ledger.Open("admission-open-session-a", "automation", "synthetic-a").Session!.Id;
        var b = ledger.Open("admission-open-session-b", "automation", "synthetic-b").Session!.Id;
        var grantA = Guid.NewGuid().ToString("N");
        var grantB = Guid.NewGuid().ToString("N");
        SessionLedgerAdmission AdmitA() => new(true, "actor-a", "automation", a, grantA);
        SessionLedgerAdmission AdmitB() => new(true, "actor-b", "agent", b, grantB);
        var changeA = new SessionLedgerCommand("set-title", a, "common-action-key", 0, "one");
        var changeB = new SessionLedgerCommand("set-title", b, "common-action-key", 0, "two");
        var first = ledger.ExecuteAdmitted(changeA, AdmitA);
        Require(first.Applied && first.Session!.Version == 1);
        Require(ledger.ExecuteAdmitted(changeB, AdmitB).Applied);
        Require(ledger.ExecuteAdmitted(changeA, AdmitA).IsReplay);
        Require(ledger.ExecuteAdmitted(changeA with { Value = "conflict" }, AdmitA).Status == "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT");
        Require(ledger.ExecuteAdmitted(changeB, AdmitA).Status == "ADMISSION_REFUSED");
        Require(ledger.ExecuteAdmitted(changeA, () => AdmitA() with { Audience = "human" }).Status == "ADMISSION_REFUSED");
        Require(ledger.ExecuteAdmitted(changeA, () => AdmitA() with { Allowed = false }).Status == "ADMISSION_REFUSED");
        Require(ledger.ExecuteAdmitted(changeA with { Key = "stale-action-key" }, AdmitA).Status == "VERSION_CONFLICT");

        using var trace = new TraceNormalizer();
        var cap = trace.CreateSession(Guid.ParseExact(a, "N"));
        trace.SetCapture(cap, TraceCaptureMode.Metadata);
        trace.TryObserve(cap, new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Local, TraceEventKind.Request));
        using var foreign = new TraceNormalizer();
        var forged = foreign.CreateSession(cap.SessionId);
        var refused = false;
        try { trace.Read(forged); } catch (UnauthorizedAccessException) { refused = true; }
        Require(refused);
        refused = false;
        try { trace.CreateSession(cap.SessionId); } catch (ArgumentException) { refused = true; }
        Require(refused);
        refused = false;
        try { trace.CreateSession(Guid.Empty); } catch (ArgumentException) { refused = true; }
        Require(refused);

        using var projectionEntered = new ManualResetEventSlim();
        using var releaseProjection = new ManualResetEventSlim();
        using var revocationStarted = new ManualResetEventSlim();
        var projection = Task.Run(() => ledger.InspectProjectionAdmitted(a, AdmitA, _ =>
        {
            projectionEntered.Set();
            Require(releaseProjection.Wait(TimeSpan.FromSeconds(5)));
            return trace.Read(cap);
        }));
        Require(projectionEntered.Wait(TimeSpan.FromSeconds(5)));
        var revocation = Task.Run(() =>
        {
            revocationStarted.Set();
            return ledger.ExecuteAdmitted(new("revoke-self", a, "unused-revoke-key"), AdmitA);
        });
        try
        {
            Require(revocationStarted.Wait(TimeSpan.FromSeconds(5)));
            try { await revocation.WaitAsync(TimeSpan.FromMilliseconds(100)); throw new InvalidOperationException("Revocation bypassed active projection gate."); }
            catch (TimeoutException) { }
        }
        finally { releaseProjection.Set(); }
        var copied = await projection.WaitAsync(TimeSpan.FromSeconds(5));
        Require(copied.Admission.Status == "OBSERVED" && copied.Projection!.Events.Count == 1);
        Require((await revocation.WaitAsync(TimeSpan.FromSeconds(5))).Status == "REVOKED");
        var callbackInvoked = false;
        var denied = ledger.InspectProjectionAdmitted(a, AdmitA, _ => { callbackInvoked = true; return trace.Read(cap); });
        Require(denied.Admission.Status == "GRANT_REVOKED" && !callbackInvoked && denied.Projection is null);
        Require(ledger.ExecuteAdmitted(changeA, AdmitA).Status == "GRANT_REVOKED");
        Require(ledger.ExecuteAdmitted(new("revoke-self", a, "unused-revoke-key"), AdmitA).Status == "GRANT_REVOKED");
        Require(ledger.ExecuteAdmitted(changeB, AdmitB).IsReplay);
        var recovered = new SharedSessionLedger(saved);
        Require(recovered.ExecuteAdmitted(changeA, AdmitA).Status == "GRANT_REVOKED");
        Require(recovered.ExecuteAdmitted(changeA, () => AdmitA() with { GrantId = Guid.NewGuid().ToString("N") }).IsReplay);
        Require(recovered.Inspect(a)!.Version == 1 && recovered.Inspect(b)!.Version == 1);
        var humanGrant = Guid.NewGuid().ToString("N");
        var humanInventoryGrant = Guid.NewGuid().ToString("N");
        Require(ledger.ExecuteAdmitted(changeA, () => new(true, "actor-a", "automation", a, grantA, HumanBootstrapVerified: true)).Status == "ADMISSION_REFUSED");
        Require(ledger.ExecuteAdmitted(changeA, () => new(true, "human-owner", "human", a, humanGrant, HumanBootstrapVerified: false)).Status == "ADMISSION_REFUSED");
        var humanOpen = ledger.ExecuteAdmitted(
            new("open", "create-only", "human-bootstrap-open-001", 0, "Human bootstrap session",
                "https://chatgpt.com/c/123e4567-e89b-42d3-a456-426614174000", "Human/Workspace"),
            () => new(true, "human-owner", "human", "create-only", humanGrant, HumanBootstrapVerified: true));
        Require(humanOpen.Applied && humanOpen.Session is { Version: 0, Title: "Human bootstrap session", WorkspaceKey: "Human/Workspace" });
        var humanSessionId = humanOpen.Session!.Id;
        var humanRename = ledger.ExecuteAdmitted(
            new("set-title", humanSessionId, "human-bootstrap-rename-001", 0, "Human renamed"),
            () => new(true, "human-owner", "human", humanSessionId, humanGrant, HumanBootstrapVerified: true));
        Require(humanRename.Applied && humanRename.Session is { Version: 1, Title: "Human renamed" });
        var invAllowed = ledger.InspectInventoryAdmitted(() => new(true, "human-owner", "human", "inventory", humanInventoryGrant, HumanBootstrapVerified: true));
        Require(invAllowed.Admission.Status == "OBSERVED" && invAllowed.Inventory is not null && invAllowed.Inventory.Sessions.Count == 3);
        var invWrongSession = ledger.InspectInventoryAdmitted(() => new(true, "human-owner", "human", humanSessionId, humanInventoryGrant, HumanBootstrapVerified: true));
        Require(invWrongSession.Admission.Status == "ADMISSION_REFUSED" && invWrongSession.Inventory is null);

        var capLedger = new SharedSessionLedger();
        for (var i = 0; i < SharedSessionLedger.MaxWorkbenchVisibleSessions; i++)
        {
            var capRes = capLedger.ExecuteAdmitted(
                new("open", "create-only", $"cap-session-open-{i:D3}", 0, $"Session {i + 1}"),
                () => new(true, "human-owner", "human", "create-only", humanGrant, HumanBootstrapVerified: true));
            Require(capRes.Applied);
        }
        var overflow = capLedger.ExecuteAdmitted(
            new("open", "create-only", "cap-session-open-overflow", 0, "Session 17"),
            () => new(true, "human-owner", "human", "create-only", humanGrant, HumanBootstrapVerified: true));
        Require(!overflow.Applied && !overflow.IsReplay && overflow.Status == "SESSION_CAPACITY_REACHED");
        var overflowReplay = capLedger.ExecuteAdmitted(
            new("open", "create-only", "cap-session-open-overflow", 0, "Session 17"),
            () => new(true, "human-owner", "human", "create-only", humanGrant, HumanBootstrapVerified: true));
        Require(!overflowReplay.Applied && overflowReplay.IsReplay && overflowReplay.Status == "SESSION_CAPACITY_REACHED");
        var overflowConflict = capLedger.ExecuteAdmitted(
            new("open", "create-only", "cap-session-open-overflow", 0, "Session 17 altered"),
            () => new(true, "human-owner", "human", "create-only", humanGrant, HumanBootstrapVerified: true));
        Require(!overflowConflict.Applied && overflowConflict.Status == "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT");
        Require(LedgerWorkbenchProjection.Read(capLedger).Status == "OBSERVED");

        Console.WriteLine("Peer/session admission checks PASS (shared gate, scoped replay, revocation, isolated trace projection)");
    }
}
