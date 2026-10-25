namespace SupraChat.Core;

public enum MetadataConsentScope { SyntheticMetadata, FirstPartyMetadata, Payload }
public sealed record MetadataConsentReview(Guid PlanId, Guid SessionId, long ViewRevision,
    MetadataConsentScope Scope, bool Available, string Reason, DateTimeOffset ReviewExpiresAt,
    int LeaseSeconds = 120, string Retention = "BOUNDED_VOLATILE_METADATA",
    bool PayloadCapture = false, bool AutomaticUpload = false, bool HumanIdentityVerified = false);
public sealed record MetadataConsentResult(Guid PlanId, Guid SessionId, string Outcome, string Reason,
    DateTimeOffset? OriginalLeaseExpiresAt = null, bool ImmutableFirstOutcome = true);
public sealed record MetadataConsentStatus(Guid SessionId, long ViewRevision, string State,
    bool CaptureActive, int RetainedEvents, long RefusedObservations, bool FirstPartyAdmission = false);

/// <summary>Owner-process object capability. UUIDs and serialized review objects are not capabilities.</summary>
public sealed class MetadataConsentBinding
{
    public Guid SessionId { get; }
    internal MetadataConsentBinding(Guid sessionId) => SessionId = sessionId;
}
public sealed class MetadataConsentPlan
{
    public MetadataConsentReview Review { get; }
    internal MetadataConsentPlan(MetadataConsentReview review) => Review = review;
}

/// <summary>
/// Volatile, synthetic-only consent workflow. Host callers own registration and GUI confirmation.
/// Every producer must use this gateway; it does not authorize the existing native adapter or a human.
/// Lock order is consent then normalizer; callbacks into the ledger or external I/O are prohibited.
/// </summary>
public sealed class MetadataCaptureConsent : IDisposable
{
    private sealed class BindingState(LocalTraceSession capability, object view, bool nativeSyntheticEnabled)
    {
        public readonly LocalTraceSession Capability = capability;
        public object View = view;
        public readonly bool NativeSyntheticEnabled = nativeSyntheticEnabled;
        public long Revision, Epoch, LeaseStarted, Refused;
        public bool Active;
        public TaskCompletionSource<bool>? Revoked;
        public ITimer? LeaseTimer;
        public string State = "OFF";
    }
    private sealed class PlanState(MetadataConsentBinding binding, long epoch, long started)
    {
        public readonly MetadataConsentBinding Binding = binding;
        public readonly long Epoch = epoch, Started = started;
        public MetadataConsentResult? Result;
    }
    private readonly object _gate = new();
    private readonly TraceNormalizer _normalizer;
    private readonly TimeProvider _time;
    private readonly Dictionary<MetadataConsentBinding, BindingState> _bindings = new();
    private readonly Dictionary<MetadataConsentPlan, PlanState> _plans = new();
    private bool _disposed;
    public MetadataCaptureConsent(TraceNormalizer normalizer, TimeProvider? time = null)
    { _normalizer = normalizer; _time = time ?? TimeProvider.System; }

    public MetadataConsentBinding Register(LocalTraceSession capability, object view) => RegisterCore(capability, view, false);

    // Native synthetic WebView frames require explicit registration; this is not first-party authorization.
    public MetadataConsentBinding RegisterNativeSynthetic(LocalTraceSession capability, object view) => RegisterCore(capability, view, true);

    private MetadataConsentBinding RegisterCore(LocalTraceSession capability, object view, bool nativeSyntheticEnabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(view);
            if (_bindings.Count >= 16 || _bindings.Values.Any(b => b.Capability.SessionId == capability.SessionId))
                throw new InvalidOperationException("CONSENT_BINDING_UNAVAILABLE");
            var page = _normalizer.Read(capability, limit: 1);
            if (page.Mode != TraceCaptureMode.Off || page.Events.Count != 0)
                throw new UnauthorizedAccessException("CONSENT_REQUIRES_FRESH_OFF_SESSION");
            var binding = new MetadataConsentBinding(capability.SessionId);
            _bindings.Add(binding, new(capability, view, nativeSyntheticEnabled));
            return binding;
        }
    }

    private BindingState Authorize(MetadataConsentBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_bindings.TryGetValue(binding, out var state)) throw new UnauthorizedAccessException("CONSENT_BINDING_REFUSED");
        Refresh(state);
        return state;
    }

    public MetadataConsentPlan Preview(MetadataConsentBinding binding, MetadataConsentScope scope = MetadataConsentScope.SyntheticMetadata)
    {
        lock (_gate)
        {
            var state = Authorize(binding);
            if (_plans.Count >= 64) throw new InvalidOperationException("CONSENT_REVIEW_CAPACITY_REACHED");
            var available = scope == MetadataConsentScope.SyntheticMetadata;
            var review = new MetadataConsentReview(Guid.NewGuid(), binding.SessionId, state.Revision, scope, available,
                available ? "SYNTHETIC_METADATA_REVIEW" : "CAPTURE_SCOPE_UNAVAILABLE", _time.GetUtcNow().AddSeconds(60));
            var plan = new MetadataConsentPlan(review);
            _plans.Add(plan, new(binding, state.Epoch, _time.GetTimestamp()));
            return plan;
        }
    }

    public MetadataConsentResult Confirm(MetadataConsentBinding binding, MetadataConsentPlan plan, bool reviewed)
    {
        lock (_gate)
        {
            var state = Authorize(binding);
            if (!_plans.TryGetValue(plan, out var pending)) throw new UnauthorizedAccessException("CONSENT_PLAN_REFUSED");
            if (!ReferenceEquals(binding, pending.Binding))
                return new(plan.Review.PlanId, binding.SessionId, "REFUSED", "WRONG_SESSION_OR_VIEW", ImmutableFirstOutcome: false);
            if (pending.Result is not null) return pending.Result; // Historical first outcome; never extends the original lease.
            string? refusal = !plan.Review.Available ? "CAPTURE_SCOPE_UNAVAILABLE" : !reviewed ? "REVIEW_NOT_ACKNOWLEDGED" :
                state.Revision != plan.Review.ViewRevision ? "VIEW_CHANGED" : state.Epoch != pending.Epoch ? "REVIEW_INVALIDATED" :
                _time.GetElapsedTime(pending.Started) >= TimeSpan.FromSeconds(60) ? "REVIEW_EXPIRED" : null;
            if (refusal is not null) return pending.Result = new(plan.Review.PlanId, plan.Review.SessionId, "REFUSED", refusal);
            // A renewed review revokes its previous lease before enabling replacement capture.
            if (state.Active) StopState(state, "REPLACED");
            try { _normalizer.SetCapture(state.Capability, TraceCaptureMode.Metadata); }
            catch { return pending.Result = new(plan.Review.PlanId, plan.Review.SessionId, "REFUSED", "RECORDER_UNAVAILABLE"); }
            state.Active = true; state.State = "ACTIVE"; state.LeaseStarted = _time.GetTimestamp();
            state.Revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (state.NativeSyntheticEnabled)
                state.LeaseTimer = _time.CreateTimer(_ => ExpireNativeLease(binding, state), null,
                    TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
            return pending.Result = new(plan.Review.PlanId, binding.SessionId, "ALLOWED", "SYNTHETIC_METADATA_LEASE_STARTED", _time.GetUtcNow().AddSeconds(120));
        }
    }

    public void CancelPreview(MetadataConsentPlan plan)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_plans.TryGetValue(plan, out var pending)) throw new UnauthorizedAccessException("CONSENT_PLAN_REFUSED");
            pending.Result ??= new(plan.Review.PlanId, plan.Review.SessionId, "REFUSED", "REVIEW_CANCELLED");
        }
    }

    public bool IsPending(MetadataConsentBinding binding, MetadataConsentPlan plan)
    {
        lock (_gate)
        {
            var state = Authorize(binding);
            return _plans.TryGetValue(plan, out var pending) && ReferenceEquals(binding, pending.Binding) &&
                pending.Result is null && plan.Review.Available && state.Revision == plan.Review.ViewRevision &&
                state.Epoch == pending.Epoch && _time.GetElapsedTime(pending.Started) < TimeSpan.FromSeconds(60);
        }
    }

    // Timer callback never touches the native COM object. It only signals the async lease.
    private void ExpireNativeLease(MetadataConsentBinding binding, BindingState expected)
    {
        lock (_gate)
        {
            if (_disposed || !_bindings.TryGetValue(binding, out var current) ||
                !ReferenceEquals(current, expected) || !current.Active) return;
            StopState(current, "EXPIRED");
        }
    }

    private void StopState(BindingState state, string reason)
    {
        state.Active = false; state.State = reason; state.Epoch++;
        state.LeaseTimer?.Dispose();
        state.LeaseTimer = null;
        state.Revoked?.TrySetResult(true);
        state.Revoked = null;
        try { _normalizer.SetCapture(state.Capability, TraceCaptureMode.Off); } catch { state.State = "RECORDER_UNAVAILABLE"; }
    }
    private void Refresh(BindingState state)
    {
        if (!state.Active) return;
        if (_time.GetElapsedTime(state.LeaseStarted) >= TimeSpan.FromSeconds(120)) { StopState(state, "EXPIRED"); return; }
        try { if (!_normalizer.IsCapturing(state.Capability)) StopState(state, "RECORDER_STOPPED"); }
        catch { StopState(state, "RECORDER_UNAVAILABLE"); }
    }
    public void Stop(MetadataConsentBinding binding)
    { lock (_gate) StopState(Authorize(binding), "STOPPED"); }
    public void ReplaceView(MetadataConsentBinding binding, object oldView, object newView)
    {
        lock (_gate)
        {
            var state = Authorize(binding);
            if (!ReferenceEquals(state.View, oldView)) throw new UnauthorizedAccessException("CONSENT_VIEW_REFUSED");
            ArgumentNullException.ThrowIfNull(newView);
            StopState(state, "VIEW_CHANGED"); state.View = newView; state.Revision++;
        }
    }

    /// <summary>Read-only native attachment preflight. Never admits a first-party origin or payload.</summary>
    /// <summary>
    /// Provides the one-shot revocation signal for exactly this active view/lease.
    /// The signal completes asynchronously on stop, view replacement, expiration,
    /// recorder failure, reapproval, or disposal. No UI/COM callback runs under _gate.
    /// </summary>
    public Task GetNativeRevocationTask(MetadataConsentBinding binding, object view)
    {
        lock (_gate)
        {
            if (_disposed || !_bindings.TryGetValue(binding, out var state)) 
                throw new UnauthorizedAccessException("CONSENT_BINDING_REFUSED");
            Refresh(state);
            if (!state.NativeSyntheticEnabled || !state.Active ||
                !ReferenceEquals(state.View, view) || state.Revoked is null ||
                !_normalizer.IsCapturing(state.Capability))
                throw new UnauthorizedAccessException("NATIVE_CONSENT_NOT_ACTIVE");
            return state.Revoked.Task;
        }
    }

    public bool IsNativeSyntheticAdmitted(MetadataConsentBinding binding, object view)
    {
        lock (_gate)
        {
            if (_disposed || !_bindings.TryGetValue(binding, out var state)) return false;
            Refresh(state);
            return state.NativeSyntheticEnabled && state.Active &&
                ReferenceEquals(state.View, view) && _normalizer.IsCapturing(state.Capability);
        }
    }

    public bool TryObserve(MetadataConsentBinding binding, object view, TraceObservation observation)
    {
        lock (_gate)
        {
            if (_disposed || !_bindings.TryGetValue(binding, out var state)) return false;
            Refresh(state);
            if (!state.Active || !ReferenceEquals(state.View, view) || observation is null ||
                observation.ProviderSurface != TraceSurface.Synthetic ||
                (observation.Adapter != TraceAdapter.Fixture &&
                 !(state.NativeSyntheticEnabled && observation.Adapter == TraceAdapter.WebView2)))
            { state.Refused++; return false; }
            try
            {
                var observed = _normalizer.TryObserve(state.Capability, observation);
                if (!observed) state.Refused++;
                return observed;
            }
            catch { state.Refused++; StopState(state, "RECORDER_UNAVAILABLE"); return false; }
        }
    }

    public MetadataConsentStatus Status(MetadataConsentBinding binding)
    {
        lock (_gate)
        {
            var state = Authorize(binding);
            var capture = _normalizer.CaptureState(state.Capability);
            return new(binding.SessionId, state.Revision, state.State, state.Active && capture.Mode == TraceCaptureMode.Metadata, capture.Count, state.Refused);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var state in _bindings.Values) StopState(state, "DISPOSED");
            _plans.Clear(); _bindings.Clear();
        }
    }
}
