using SupraChat.Core;

namespace SupraChat.ContractTests;

/// <summary>
/// Portable synthetic oracle for one-owner, multi-actor ledger semantics.
/// This is not an OS peer/issuer or production shared-host qualification.
/// </summary>
internal static class SharedSessionAdversarialChecks
{
    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException("S0_LEDGER_" + failure);
    }

    public static void Run()
    {
        SessionLedgerCheckpoint? checkpoint = null;
        var ledger = new SharedSessionLedger(persist: state => checkpoint = state);
        var createGrant = Guid.NewGuid().ToString("N");
        SessionLedgerAdmission CreateAdmission() =>
            new(true, "synthetic-owner", "automation", "create-only", createGrant);
        var open = ledger.ExecuteAdmitted(
            new("open", "create-only", "s0-create-initial-session", 0, "Synthetic S0 session"),
            CreateAdmission);
        Require(open.Applied && open.Session is {Version: 0} && checkpoint is not null,
            "CREATE_FAILED");
        var id = open.Session!.Id;

        var grantA = Guid.NewGuid().ToString("N");
        var grantB = Guid.NewGuid().ToString("N");
        SessionLedgerAdmission AdmitA() => new(true, "synthetic-automation", "automation", id, grantA);
        SessionLedgerAdmission AdmitB() => new(true, "synthetic-agent", "agent", id, grantB);
        var requestA = new SessionLedgerCommand("set-title", id, "s0-parallel-writer-alpha", 0, "Title A");
        var requestB = new SessionLedgerCommand("set-title", id, "s0-parallel-writer-bravo", 0, "Title B");

        // Two independent actor tasks deliberately compete on one expected version.
        // No request may ACK an uncommitted version or produce two successful CAS writes.
        using var start = new ManualResetEventSlim(false);
        var a = Task.Run(() => { start.Wait(); return ledger.ExecuteAdmitted(requestA, AdmitA); });
        var b = Task.Run(() => { start.Wait(); return ledger.ExecuteAdmitted(requestB, AdmitB); });
        start.Set();
        Task.WaitAll(a, b);
        var outcomes = new[] {a.Result, b.Result};
        Require(outcomes.Count(r => r.Status == "APPLIED" && r.Applied) == 1 &&
                outcomes.Count(r => r.Status == "VERSION_CONFLICT" && !r.Applied) == 1,
            "RACE_NOT_SERIALIZED");
        Require(ledger.Inspect(id)!.Version == 1 && checkpoint!.Sessions.Single().Version == 1,
            "UNCOMMITTED_STATE_EXPOSED");

        var winner = a.Result.Applied ? requestA : requestB;
        var winnerAdmit = a.Result.Applied ? (Func<SessionLedgerAdmission>)AdmitA : AdmitB;
        var winnerGrant = a.Result.Applied ? grantA : grantB;
        var winnerActor = a.Result.Applied ? "synthetic-automation" : "synthetic-agent";
        var loser = a.Result.Applied ? requestB : requestA;
        var loserAdmit = a.Result.Applied ? (Func<SessionLedgerAdmission>)AdmitB : AdmitA;
        var first = outcomes.Single(r => r.Applied);
        var exactRetry = ledger.ExecuteAdmitted(winner, winnerAdmit);
        Require(exactRetry.Applied && exactRetry.IsReplay &&
                exactRetry.Session == first.Session && exactRetry.Sequence == first.Sequence,
            "DUPLICATE_EFFECT");

        var changedPayload = ledger.ExecuteAdmitted(winner with {Value = "Tampered payload"},
            winnerAdmit);
        Require(!changedPayload.Applied && changedPayload.Status == "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT",
            "CHANGED_PAYLOAD_ACCEPTED");
        var deniedReplay = ledger.ExecuteAdmitted(loser, loserAdmit);
        Require(!deniedReplay.Applied && deniedReplay.IsReplay &&
                deniedReplay.Status == "VERSION_CONFLICT",
            "DENIED_OUTCOME_NOT_STABLE");

        // The same human-intent operation key belongs to a verified actor, not
        // to an arbitrary caller. A distinct actor with the same raw key must
        // occupy a distinct idempotency namespace (and still pass CAS).
        var otherAdmit = a.Result.Applied ? (Func<SessionLedgerAdmission>)AdmitB : AdmitA;
        var second = ledger.ExecuteAdmitted(
            winner with {ExpectedVersion = 1, Value = "Distinct actor", Key = winner.Key},
            otherAdmit);
        Require(second.Applied && second.Session!.Version == 2 && ledger.Inspect(id)!.Version == 2,
            "ACTOR_NAMESPACE_COLLISION");

        // Revocation beats even historically cached success for the revoked
        // capability. A renewed, separately verified grant for the same actor
        // may query the durable first outcome without performing another write.
        var revoked = ledger.RevokeOwnGrant(winnerGrant, id,
            a.Result.Applied ? "automation" : "agent");
        Require(revoked && ledger.IsGrantRevoked(winnerGrant), "REVOKE_FAILED");
        var revokedReplay = ledger.ExecuteAdmitted(winner, winnerAdmit);
        Require(revokedReplay.Status == "GRANT_REVOKED" && !revokedReplay.Applied,
            "REVOKED_REPLAY_ALLOWED");
        var copied = false;
        var blockedRead = ledger.InspectProjectionAdmitted(id, winnerAdmit, state =>
        {
            copied = true;
            return state.Version;
        });
        Require(blockedRead.Admission.Status == "GRANT_REVOKED" && !copied,
            "REVOKED_PROJECTION_COPIED");

        // Simulate a lost transport acknowledgement after durable checkpoint:
        // reconstruct independently from the store snapshot, then resubmit the
        // *original* actor-scoped operation under a fresh valid grant.
        Require(checkpoint!.RevokedGrantIds!.Contains(winnerGrant), "REVOCATION_NOT_PERSISTED");
        var recovered = new SharedSessionLedger(checkpoint,
            persist: state => checkpoint = state);
        Require(recovered.Inspect(id)!.Version == 2 &&
                recovered.IsGrantRevoked(winnerGrant) &&
                recovered.InspectAll().Events.Count == ledger.InspectAll().Events.Count,
            "RESTART_RECOVERY_MISMATCH");
        var newGrant = Guid.NewGuid().ToString("N");
        SessionLedgerAdmission RenewedAdmit() =>
            new(true, winnerActor, a.Result.Applied ? "automation" : "agent", id, newGrant);
        var recoveredRetry = recovered.ExecuteAdmitted(winner, RenewedAdmit);
        Require(recoveredRetry.Applied && recoveredRetry.IsReplay &&
                recoveredRetry.Sequence == first.Sequence &&
                recoveredRetry.Session == first.Session &&
                recovered.Inspect(id)!.Version == 2,
            "LOST_ACK_DOUBLE_COMMIT");

        var forgeryCopied = false;
        var forged = recovered.InspectProjectionAdmitted(id,
            () => new SessionLedgerAdmission(false, "unpaired", "agent", id, Guid.NewGuid().ToString("N")),
            _ => { forgeryCopied = true; return true; });
        Require(forged.Admission.Status == "ADMISSION_REFUSED" && !forgeryCopied,
            "UNPAIRED_READ_ACCEPTED");

        var events = recovered.InspectAll().Events;
        Require(events.Count == 4 &&
                events.Select(e => e.Sequence).SequenceEqual(new long[] {1, 2, 3, 4}) &&
                events.Count(e => e.Operation == "set-title") == 2,
            "EVENT_SEQUENCE_INCONSISTENT");
        Require(events.All(e => e.SessionId == id), "CROSS_SESSION_EVENT_LEAK");
    }
}
