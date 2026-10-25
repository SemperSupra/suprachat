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
/// Volatile consent workflow. Public registration admits fixture producers only; internal native
/// registration binds the guarded adapter to its current view and plan. Hosts own GUI confirmation.
/// Consent does not verify a human identity or authorize provider operations.
/// Lock order is consent then normalizer; callbacks into the ledger or external I/O are prohibited.
/// </summary>
public sealed class MetadataCaptureConsent : IDisposable
{
    internal enum Producer { Fixture, SyntheticNative, FirstPartyNative }
    private sealed class BindingState(LocalTraceSession capability, object view)
    {
        public readonly LocalTraceSession Capability = capability;
        public object View = view;
        public long Revision, Epoch, LeaseStarted, Refused;
        public bool Active;
        public Producer Source;
        public MetadataConsentPlan? ActivePlan;
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

    public MetadataConsentBinding Register(LocalTraceSession capability, object view) => RegisterCore(capability, view, Producer.Fixture);
    internal MetadataConsentBinding RegisterNative(LocalTraceSession capability, object view, bool firstParty) =>
        RegisterCore(capability, view, firstParty ? Producer.FirstPartyNative : Producer.SyntheticNative);
    private MetadataConsentBinding RegisterCore(LocalTraceSession capability, object view, Producer producer)
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
            _bindings.Add(binding, new(capability, view) { Source = producer });
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
            var available = scope == MetadataConsentScope.SyntheticMetadata && state.Source != Producer.FirstPartyNative ||
                scope == MetadataConsentScope.FirstPartyMetadata && state.Source == Producer.FirstPartyNative;
            var review = new MetadataConsentReview(Guid.NewGuid(), binding.SessionId, state.Revision, scope, available,
                available ? (scope == MetadataConsentScope.FirstPartyMetadata ? "FIRST_PARTY_METADATA_REVIEW" : "SYNTHETIC_METADATA_REVIEW") :
                    "CAPTURE_SCOPE_UNAVAILABLE", _time.GetUtcNow().AddSeconds(60));
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
            try { _normalizer.SetCapture(state.Capability, TraceCaptureMode.Metadata); }
            catch { return pending.Result = new(plan.Review.PlanId, plan.Review.SessionId, "REFUSED", "RECORDER_UNAVAILABLE"); }
            state.Active = true; state.State = "ACTIVE"; state.LeaseStarted = _time.GetTimestamp();
            state.ActivePlan = plan;
            return pending.Result = new(plan.Review.PlanId, binding.SessionId, "ALLOWED",
                state.Source == Producer.FirstPartyNative ? "FIRST_PARTY_METADATA_LEASE_STARTED" : "SYNTHETIC_METADATA_LEASE_STARTED", _time.GetUtcNow().AddSeconds(120));
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

    private void StopState(BindingState state, string reason)
    {
        state.Active = false; state.ActivePlan = null; state.State = reason; state.Epoch++;
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

    public bool TryObserve(MetadataConsentBinding binding, object view, TraceObservation observation)
        => Observe(binding, view, null, null, observation, native: false);

    private bool Observe(MetadataConsentBinding binding, object view, MetadataConsentPlan? plan,
        LocalTraceSession? capability, TraceObservation observation, bool native)
    {
        lock (_gate)
        {
            if (_disposed || !_bindings.TryGetValue(binding, out var state)) return false;
            Refresh(state);
            var sourceMatches = native ? state.Source != Producer.Fixture && ReferenceEquals(state.ActivePlan, plan) &&
                ReferenceEquals(state.Capability, capability) && observation?.Adapter == TraceAdapter.WebView2 &&
                observation.ProviderSurface == (state.Source == Producer.FirstPartyNative ? TraceSurface.ChatGptWebView : TraceSurface.Synthetic) :
                state.Source == Producer.Fixture && observation?.ProviderSurface == TraceSurface.Synthetic && observation.Adapter == TraceAdapter.Fixture;
            if (!state.Active || !ReferenceEquals(state.View, view) || !sourceMatches || observation is null)
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

    internal bool NativeLeaseActive(MetadataConsentBinding binding, object view, MetadataConsentPlan plan)
    {
        lock (_gate)
        {
            if (_disposed || !_bindings.TryGetValue(binding, out var state)) return false;
            Refresh(state);
            return state.Source != Producer.Fixture && state.Active && ReferenceEquals(state.View, view) && ReferenceEquals(state.ActivePlan, plan);
        }
    }
    internal TimeSpan NativeLeaseRemaining(MetadataConsentBinding binding, object view, MetadataConsentPlan plan)
    {
        lock (_gate) return NativeLeaseActive(binding, view, plan)
            ? TimeSpan.FromSeconds(120) - _time.GetElapsedTime(_bindings[binding].LeaseStarted) : TimeSpan.Zero;
    }
    internal ITraceSink NativeSink(MetadataConsentBinding binding, object view, MetadataConsentPlan plan) => new GuardedSink(this, binding, view, plan);
    private sealed class GuardedSink(MetadataCaptureConsent owner, MetadataConsentBinding binding, object view, MetadataConsentPlan plan) : ITraceSink
    {
        public bool TryObserve(LocalTraceSession session, TraceObservation observation) => owner.Observe(binding, view, plan, session, observation, native: true);
    }

    public MetadataConsentStatus Status(MetadataConsentBinding binding)
    {
        lock (_gate)
        {
            var state = Authorize(binding);
            var capture = _normalizer.CaptureState(state.Capability);
            return new(binding.SessionId, state.Revision, state.State, state.Active && capture.Mode == TraceCaptureMode.Metadata,
                capture.Count, state.Refused, state.Active && state.Source == Producer.FirstPartyNative);
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
