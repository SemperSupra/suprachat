namespace SupraChat.Core;

/// <summary>Host-supplied, action-specific admission. A metadata grant alone is insufficient.</summary>
public sealed record CancellationAdmission(SessionLedgerAdmission Actor, bool CancellationAllowed);
public sealed record DurableCancellationPlan(Guid PlanId, string SessionId, Guid TargetId, Guid HostRunId,
    long ExpectedSessionVersion, long ExpectedTargetRevision, string ActorId, string Audience, string GrantId,
    string ActionKey, DateTimeOffset ExpiresAt);
public sealed record DurableCancellationStage(long Sequence, string Stage, TraceOutcome Outcome, bool LocalSignalVerified);
public sealed record DurableCancellationEntry(DurableCancellationPlan Plan, bool? Confirmed, DurableCancellationStage[] Stages);
public sealed record DurableCancellationResult(string Status, bool IsReplay, DurableCancellationEntry? Entry)
{
    public string Schema => "suprachat-durable-owned-cancellation/v1";
    public bool ProviderStopVerified => false;
    public bool CallbackCompletionVerified => false;
    public string Authority => "HOST_ADMITTED_SINGLE_LEDGER";
}

public sealed partial class SharedSessionLedger
{
    private const int MaxCancellationRecords = 128, MaxCancellationTargets = 64;
    private readonly Guid _cancellationRun = Guid.NewGuid();
    private readonly TimeProvider _controlClock;
    private readonly Dictionary<string, DurableCancellationEntry> _cancellationJournal = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> _cancellationStarted = new();
    private readonly Dictionary<Guid, CancellationTarget> _cancellationTargets = new();
    private bool _controlCommitUncertain;
    private sealed record CancellationTarget(string SessionId, LocalOwnedStreamControls Owner,
        LocalOwnedStreamControls.OwnedStreamLease Lease);

    /// <summary>Trusted local composition with an exact live lease; serialized IDs cannot enroll an executor.</summary>
    public Guid RegisterCancellationTarget(string sessionId, LocalOwnedStreamControls owner,
        LocalOwnedStreamControls.OwnedStreamLease stream)
    {
        lock (_gate)
        {
            if (_persist is null || _controlCommitUncertain) throw new InvalidOperationException("DURABLE_CONTROL_HOST_REQUIRED");
            if (!_sessions.ContainsKey(sessionId) || !Guid.TryParseExact(sessionId, "N", out var session) ||
                !owner.IsCancellationTarget(session, stream, stream.Revision)) throw new UnauthorizedAccessException("LIVE_TARGET_REFUSED");
            foreach (var old in _cancellationTargets.Where(pair => !pair.Value.Owner.IsCancellationTarget(
                Guid.ParseExact(pair.Value.SessionId, "N"), pair.Value.Lease, pair.Value.Lease.Revision, this)).Select(pair => pair.Key).ToArray())
                _cancellationTargets.Remove(old);
            if (_cancellationTargets.Count >= MaxCancellationTargets || _cancellationTargets.ContainsKey(stream.StreamId))
                throw new InvalidOperationException("TARGET_CAPACITY_OR_DUPLICATE");
            if (!owner.TryClaimCancellation(session, stream, this)) throw new UnauthorizedAccessException("TARGET_ALREADY_CLAIMED");
            _cancellationTargets.Add(stream.StreamId, new(sessionId, owner, stream));
            return stream.StreamId;
        }
    }

    public DurableCancellationResult PreviewCancellation(string sessionId, Guid targetId, long expectedSessionVersion,
        long expectedTargetRevision, string actionKey, Func<CancellationAdmission> admit)
    {
        ValidateIdempotencyKey(actionKey);
        lock (_gate)
        {
            var actor = AdmitCancellation(sessionId, admit);
            if (actor is null) return new("CANCELLATION_ADMISSION_REFUSED", false, null);
            if (_persist is null || _controlCommitUncertain) return new("DURABLE_CONTROL_HOST_REQUIRED", false, null);
            var key = CancellationKey(actor.ActorId!, actionKey);
            if (_cancellationJournal.TryGetValue(key, out var prior))
                return Matches(prior.Plan, sessionId, targetId, expectedSessionVersion, expectedTargetRevision, actor, actionKey)
                    ? prior.Confirmed is null && prior.Plan.GrantId != actor.GrantId
                        ? new("GRANT_CHANGED_FRESH_REVIEW_REQUIRED", false, null) : HistoricalCancellation(prior)
                    : new("IDEMPOTENCY_CONFLICT", false, null);
            if (_cancellationJournal.Count >= MaxCancellationRecords) return new("CONTROL_CAPACITY", false, null);
            if (!_sessions.TryGetValue(sessionId, out var session) || session.Version != expectedSessionVersion)
                return new("SESSION_VERSION_CONFLICT", false, null);
            if (!_cancellationTargets.TryGetValue(targetId, out var target) || target.SessionId != sessionId ||
                !target.Owner.IsCancellationTarget(Guid.ParseExact(sessionId, "N"), target.Lease, expectedTargetRevision, this))
                return new("LIVE_TARGET_UNAVAILABLE", false, null);
            var plan = new DurableCancellationPlan(Guid.NewGuid(), sessionId, targetId, _cancellationRun, expectedSessionVersion,
                expectedTargetRevision, actor.ActorId!, actor.Audience!, actor.GrantId!, actionKey, _controlClock.GetUtcNow().AddMinutes(2));
            var entry = new DurableCancellationEntry(plan, null, Array.Empty<DurableCancellationStage>());
            _cancellationJournal.Add(key, entry); _cancellationStarted.Add(plan.PlanId, _controlClock.GetTimestamp());
            CommitCancellation();
            return new("CONFIRMATION_REQUIRED", false, CopyCancellationEntry(entry));
        }
    }

    public DurableCancellationResult ConfirmCancellation(string sessionId, Guid planId, Guid targetId, long expectedSessionVersion,
        long expectedTargetRevision, string actionKey, bool confirmed, Func<CancellationAdmission> admit)
    {
        ValidateIdempotencyKey(actionKey);
        lock (_gate)
        {
            var actor = AdmitCancellation(sessionId, admit);
            if (actor is null) return new("CANCELLATION_ADMISSION_REFUSED", false, null);
            if (_persist is null || _controlCommitUncertain) return new("DURABLE_CONTROL_HOST_REQUIRED", false, null);
            var key = CancellationKey(actor.ActorId!, actionKey);
            if (!_cancellationJournal.TryGetValue(key, out var entry)) return new("PLAN_UNAVAILABLE", false, null);
            var plan = entry.Plan;
            if (plan.PlanId != planId || !Matches(plan, sessionId, targetId, expectedSessionVersion, expectedTargetRevision, actor, actionKey) ||
                entry.Confirmed is { } firstConfirmation && firstConfirmation != confirmed)
                return new("IDEMPOTENCY_CONFLICT", false, null);
            if (entry.Confirmed is not null) return HistoricalCancellation(entry); // NEVER redispatch, including unresolved requests.
            if (plan.GrantId != actor.GrantId) return new("GRANT_CHANGED_FRESH_REVIEW_REQUIRED", false, null);
            var reason = !confirmed ? "CONFIRMATION_REQUIRED" : plan.HostRunId != _cancellationRun ? "HOST_RUN_CHANGED" :
                !_cancellationStarted.TryGetValue(planId, out var start) || _controlClock.GetElapsedTime(start) >= TimeSpan.FromMinutes(2) ? "PLAN_EXPIRED" :
                !_sessions.TryGetValue(sessionId, out var state) || state.Version != expectedSessionVersion ? "SESSION_VERSION_CONFLICT" : null;
            if (reason is not null) return RefuseCancellation(key, entry, confirmed, reason);
            if (!_cancellationTargets.TryGetValue(targetId, out var target) || target.SessionId != sessionId ||
                !target.Owner.IsCancellationTarget(Guid.ParseExact(sessionId, "N"), target.Lease, expectedTargetRevision, this))
                return RefuseCancellation(key, entry, confirmed, "LIVE_TARGET_UNAVAILABLE");

            // Approval and requested stage are durably committed BEFORE any local effect. Same W6/W9 checkpoint.
            _sessions[sessionId] = _sessions[sessionId] with { Version = expectedSessionVersion + 1 };
            AppendEvent(sessionId, actor.Audience!, "cancel-requested", expectedSessionVersion + 1);
            entry = entry with { Confirmed = true, Stages = new[] { new DurableCancellationStage(_sequence, "REQUESTED", TraceOutcome.Requested, false) } };
            _cancellationJournal[key] = entry; CommitCancellation();
            bool signalled;
            try { signalled = target.Owner.TrySignalCancellation(Guid.ParseExact(sessionId, "N"), target.Lease, expectedTargetRevision, this); }
            catch { signalled = false; } // Uncertain effect never becomes a verified result, and is never retried.
            if (!signalled)
            {
                AppendCancellationStage(key, entry, "EFFECT_UNKNOWN", TraceOutcome.Unknown, false);
                return HistoricalCancellation(_cancellationJournal[key]) with { IsReplay = false };
            }
            entry = AppendCancellationStage(key, entry, "APPLIED_LOCAL_SIGNAL", TraceOutcome.Applied, false);
            AppendCancellationStage(key, entry, "VERIFIED_LOCAL_SIGNAL", TraceOutcome.Verified, true);
            return HistoricalCancellation(_cancellationJournal[key]) with { IsReplay = false };
        }
    }

    private DurableCancellationEntry AppendCancellationStage(string key, DurableCancellationEntry entry, string stage, TraceOutcome outcome, bool verified)
    {
        AppendEvent(entry.Plan.SessionId, entry.Plan.Audience, "cancel-" + stage.ToLowerInvariant().Replace('_', '-'),
            _sessions[entry.Plan.SessionId].Version);
        entry = entry with { Stages = entry.Stages.Append(new(_sequence, stage, outcome, verified)).ToArray() };
        _cancellationJournal[key] = entry; CommitCancellation(); return entry;
    }
    private DurableCancellationResult RefuseCancellation(string key, DurableCancellationEntry entry, bool confirmed, string reason)
    {
        entry = entry with { Confirmed = confirmed };
        entry = AppendCancellationStage(key, entry, reason, TraceOutcome.Refused, false);
        return new(reason, false, CopyCancellationEntry(entry));
    }
    private void CommitCancellation()
    {
        try { _persist!(CaptureCheckpoint()); }
        catch { _controlCommitUncertain = true; throw; } // Qualified host MUST fail-stop on uncertain writes.
    }
    private SessionLedgerAdmission? AdmitCancellation(string sessionId, Func<CancellationAdmission> admit)
    {
        var admission = admit(); var actor = admission.Actor;
        return admission.CancellationAllowed && actor.Allowed && actor.SessionId == sessionId &&
            actor.Audience is "human" or "automation" or "agent" && !string.IsNullOrEmpty(actor.ActorId) && actor.ActorId.Length <= 80 &&
            actor.ActorId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
            Guid.TryParseExact(actor.GrantId, "N", out _) && !_revokedGrants.Contains(actor.GrantId)
            ? actor : null;
    }
    private static bool Matches(DurableCancellationPlan plan, string session, Guid target, long version, long targetRevision,
        SessionLedgerAdmission actor, string key) => plan.SessionId == session && plan.TargetId == target &&
        plan.ExpectedSessionVersion == version && plan.ExpectedTargetRevision == targetRevision && plan.ActorId == actor.ActorId &&
        plan.Audience == actor.Audience && plan.ActionKey == key;
    private static string CancellationKey(string actor, string key) => actor + ":" + key;
    private static DurableCancellationEntry CopyCancellationEntry(DurableCancellationEntry entry) => entry with { Stages = entry.Stages.ToArray() };
    private static DurableCancellationResult HistoricalCancellation(DurableCancellationEntry entry)
    {
        var last = entry.Stages.LastOrDefault();
        var status = last is null ? "CONFIRMATION_REQUIRED" : last.Stage is "REQUESTED" or "EFFECT_UNKNOWN"
            ? "OUTCOME_UNKNOWN_FRESH_APPROVAL_REQUIRED" : last.Stage;
        return new(status, true, CopyCancellationEntry(entry));
    }

    private void RestoreCancellationJournal(DurableCancellationEntry[]? entries)
    {
        if (entries is null) return; // Old checkpoints have no cancellation authority.
        if (entries.Length > MaxCancellationRecords) throw new InvalidDataException("CONTROL_CHECKPOINT_CAPACITY");
        var plans = new HashSet<Guid>(); var sequences = new HashSet<long>();
        foreach (var entry in entries)
        {
            var plan = entry.Plan;
            if (plan is null || plan.PlanId == Guid.Empty || !plans.Add(plan.PlanId) || plan.HostRunId == Guid.Empty || plan.TargetId == Guid.Empty ||
                !_sessions.ContainsKey(plan.SessionId) || plan.ExpectedSessionVersion < 0 || plan.ExpectedTargetRevision < 0 ||
                string.IsNullOrEmpty(plan.ActorId) || plan.ActorId.Length > 80 || plan.ActorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
                plan.Audience is not ("human" or "automation" or "agent") || !Guid.TryParseExact(plan.GrantId, "N", out _) ||
                entry.Stages is null || entry.Stages.Length > 3 || (entry.Confirmed is null) != (entry.Stages.Length == 0))
                throw new InvalidDataException("INVALID_CONTROL_CHECKPOINT");
            ValidateIdempotencyKey(plan.ActionKey);
            long previous = 0;
            foreach (var stage in entry.Stages)
            {
                if (stage.Sequence <= previous || stage.Sequence > _sequence || !sequences.Add(stage.Sequence) ||
                    !ValidCancellationStage(stage) || entry.Confirmed == false && stage.Outcome != TraceOutcome.Refused)
                    throw new InvalidDataException("INVALID_CONTROL_STAGE");
                previous = stage.Sequence;
            }
            var pattern = string.Join(',', entry.Stages.Select(s => s.Stage));
            if (entry.Stages.Length != 0 && pattern is not ("REQUESTED" or "REQUESTED,APPLIED_LOCAL_SIGNAL" or
                "REQUESTED,APPLIED_LOCAL_SIGNAL,VERIFIED_LOCAL_SIGNAL" or "REQUESTED,EFFECT_UNKNOWN") &&
                !(entry.Stages.Length == 1 && entry.Stages[0].Outcome == TraceOutcome.Refused))
                throw new InvalidDataException("INVALID_CONTROL_STAGE_ORDER");
            if (entry.Stages.Length > 0 && entry.Stages[0].Stage == "REQUESTED" && entry.Confirmed != true)
                throw new InvalidDataException("UNAPPROVED_CONTROL_STAGE");
            if (plan.ExpectedSessionVersion > _sessions[plan.SessionId].Version ||
                entry.Stages.FirstOrDefault()?.Stage == "REQUESTED" && plan.ExpectedSessionVersion >= _sessions[plan.SessionId].Version)
                throw new InvalidDataException("CONTROL_SESSION_VERSION_MISMATCH");
            if (!_cancellationJournal.TryAdd(CancellationKey(plan.ActorId, plan.ActionKey), CopyCancellationEntry(entry)))
                throw new InvalidDataException("DUPLICATE_CONTROL_RECORD");
        }
    }
    private static bool ValidCancellationStage(DurableCancellationStage stage) => stage.Stage switch
    {
        "REQUESTED" => stage.Outcome == TraceOutcome.Requested && !stage.LocalSignalVerified,
        "APPLIED_LOCAL_SIGNAL" => stage.Outcome == TraceOutcome.Applied && !stage.LocalSignalVerified,
        "VERIFIED_LOCAL_SIGNAL" => stage.Outcome == TraceOutcome.Verified && stage.LocalSignalVerified,
        "EFFECT_UNKNOWN" => stage.Outcome == TraceOutcome.Unknown && !stage.LocalSignalVerified,
        "CONFIRMATION_REQUIRED" or "HOST_RUN_CHANGED" or "PLAN_EXPIRED" or "SESSION_VERSION_CONFLICT" or "LIVE_TARGET_UNAVAILABLE" =>
            stage.Outcome == TraceOutcome.Refused && !stage.LocalSignalVerified,
        _ => false
    };
}
