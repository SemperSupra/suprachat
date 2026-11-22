using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// R46 source-only reducer for an externally admitted single-writer session.
/// This is NOT a separate ledger, a trusted identity issuer or a tool executor.
/// It must be composed inside the current SharedSessionLedger/W9 commit gate
/// before its output can become an operational human preemption guarantee.
/// </summary>
public static class HumanInterventionPolicy
{
    public const string Schema = "suprachat-human-priority-policy/v1";
    public const string Observation = "SYNTHETIC_POLICY_ONLY";
    public static HumanControlState Initial(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
            throw new ArgumentException("SESSION_ID_REQUIRED", nameof(sessionId));
        return new(sessionId, 0, HumanControlPhase.Running, null, null, null, null);
    }

    public static HumanControlOutcome Apply(HumanControlState current,
        HumanControlRequest request, SessionLedgerAdmission verifiedHuman)
    {
        CheckState(current);
        CheckRequest(request);
        if (!AdmittedHuman(current.SessionId, verifiedHuman) ||
            request.SessionId != current.SessionId)
            return Denied("AUTHENTICATED_HUMAN_REQUIRED", current);
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            request.SessionId, request.ExpectedEpoch, request.OperationKey,
            request.Kind, request.GuidanceDigest, verifiedHuman.ActorId
        }));
        if (current.LastOperationKey == request.OperationKey)
            return current.LastOperationFingerprint == fingerprint
                ? new(Schema, "REPLAY", current, true, false, false, Observation)
                : Denied("IDEMPOTENCY_CONFLICT", current);
        if (request.ExpectedEpoch != current.Epoch)
            return Denied("STALE_HUMAN_INTENT_REQUIRES_REFRESH", current);

        HumanControlPhase phase;
        switch (request.Kind)
        {
            case "guide":
                if (request.GuidanceDigest is null ||
                    current.Phase is HumanControlPhase.StopRequested or HumanControlPhase.Stopped)
                    return Denied("GUIDANCE_OR_PHASE_REFUSED", current);
                phase = HumanControlPhase.PauseRequested;
                break;
            case "pause":
                if (request.GuidanceDigest is not null ||
                    current.Phase is HumanControlPhase.StopRequested or HumanControlPhase.Stopped)
                    return Denied("PAUSE_PHASE_REFUSED", current);
                phase = HumanControlPhase.PauseRequested;
                break;
            case "stop":
                if (request.GuidanceDigest is not null ||
                    current.Phase == HumanControlPhase.Stopped)
                    return Denied("STOP_PHASE_REFUSED", current);
                phase = HumanControlPhase.StopRequested;
                break;
            case "resume":
                // Cannot resume simply because the human pressed Resume.
                // Trusted executor quiescence/reconciliation must be verified first.
                if (request.GuidanceDigest is not null ||
                    current.Phase != HumanControlPhase.Quiescent)
                    return Denied("OWNER_QUIESCENCE_NOT_VERIFIED", current);
                phase = HumanControlPhase.Running;
                break;
            default:
                return Denied("UNKNOWN_INTENT", current);
        }
        if (current.Epoch == long.MaxValue)
            return Denied("CONTROL_EPOCH_EXHAUSTED", current);
        var newState = current with
        {
            Epoch = current.Epoch + 1,
            Phase = phase,
            GuidanceDigest = request.Kind == "guide" ? request.GuidanceDigest
                : request.Kind == "resume" ? null : current.GuidanceDigest,
            LastOperationKey = request.OperationKey,
            LastOperationFingerprint = fingerprint,
            LastHumanActorId = verifiedHuman.ActorId
        };
        return new(Schema,
            phase == HumanControlPhase.Running ? "RESUME_REQUEST_ACCEPTED" :
            phase == HumanControlPhase.StopRequested ? "STOP_REQUESTED_NOT_VERIFIED" :
            "PAUSE_REQUESTED_NOT_VERIFIED", newState, false,
            true, false, Observation);
    }

    /// <summary>
    /// A proposing automation/agent may not advance with a stale epoch.
    /// Even PASS does NOT authorize real tool effects or external provider sends.
    /// An existing external admission/scope/owned target must separately admit.
    /// </summary>
    public static HumanControlFence CheckProposal(HumanControlState current,
        long observedEpoch, SessionLedgerAdmission verifiedActor)
    {
        CheckState(current);
        if (!verifiedActor.Allowed ||
            verifiedActor.SessionId != current.SessionId ||
            verifiedActor.Audience is not ("automation" or "agent") ||
            verifiedActor.HumanBootstrapVerified ||
            string.IsNullOrWhiteSpace(verifiedActor.ActorId) ||
            !Guid.TryParseExact(verifiedActor.GrantId, "N", out _))
            return new("AUTHENTICATED_ACTOR_REQUIRED", false, false);
        if (observedEpoch != current.Epoch)
            return new("STALE_EPOCH_FENCED", false, false);
        if (current.Phase != HumanControlPhase.Running)
            return new("HUMAN_INTERVENTION_FENCED", false, false);
        return new("PROPOSAL_MAY_CONTINUE_SEPARATE_TOOL_ADMISSION_REQUIRED",
            true, false);
    }

    private static bool AdmittedHuman(string session, SessionLedgerAdmission actor) =>
        actor.Allowed && actor.Audience == "human" && actor.HumanBootstrapVerified &&
        actor.SessionId == session && !string.IsNullOrWhiteSpace(actor.ActorId) &&
        actor.ActorId.Length <= 80 &&
        Guid.TryParseExact(actor.GrantId, "N", out _);

    private static void CheckState(HumanControlState s)
    {
        if (!Guid.TryParseExact(s.SessionId, "N", out _) || s.Epoch < 0 ||
            !Enum.IsDefined(s.Phase))
            throw new ArgumentException("INVALID_CONTROL_STATE");
    }
    private static void CheckRequest(HumanControlRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.OperationKey) ||
            r.OperationKey.Length is < 12 or > 128 ||
            r.OperationKey.Any(c => !char.IsAsciiLetterOrDigit(c) &&
                     c is not '-' and not '_' and not ':' and not '.') ||
            !Guid.TryParseExact(r.SessionId, "N", out _) ||
            r.ExpectedEpoch < 0 ||
            (r.GuidanceDigest is not null &&
                (r.GuidanceDigest.Length != 64 ||
                 r.GuidanceDigest.Any(c => !Uri.IsHexDigit(c) ||
                                      char.IsUpper(c)))))
            throw new ArgumentException("INVALID_HUMAN_CONTROL_ENVELOPE");
    }
    private static HumanControlOutcome Denied(string status, HumanControlState s) =>
        new(Schema, status, s, false, false, false, Observation);
    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
public enum HumanControlPhase
{
    Running,
    PauseRequested,
    Quiescent,
    StopRequested,
    Stopped
}
public sealed record HumanControlState(string SessionId, long Epoch,
    HumanControlPhase Phase, string? GuidanceDigest, string? LastOperationKey,
    string? LastOperationFingerprint, string? LastHumanActorId);
public sealed record HumanControlRequest(string SessionId, long ExpectedEpoch,
    string OperationKey, string Kind, string? GuidanceDigest = null);
public sealed record HumanControlOutcome(string Schema, string Status,
    HumanControlState State, bool IsReplay, bool StaleWorkFenced,
    bool ExternalStopVerified, string EvidenceClass);
public sealed record HumanControlFence(string Status, bool ProposalMayContinue,
    bool ToolAuthorityGranted);
