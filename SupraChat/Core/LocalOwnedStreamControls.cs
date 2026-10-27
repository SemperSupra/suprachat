namespace SupraChat.Core;

public enum OwnedControlAction { Cancel, SyntheticDelay, SyntheticDrop, SyntheticFault, SyntheticReplay, ModifyFirstParty, CapturePayload }
public sealed record OwnedStreamState(Guid StreamId, TraceSurface Surface, long Revision, bool CancellationRequested)
{
    public override string ToString() => $"{Surface} · {StreamId.ToString("N")[..8]} · {(CancellationRequested ? "stopping" : "active")}";
}
public sealed record OwnedControlPlan(Guid PlanId, Guid SessionId, Guid StreamId, OwnedControlAction Action,
    long ExpectedRevision, Guid ActionKey, DateTimeOffset ExpiresAt, TraceOutcome Outcome, string Reason);
public sealed record OwnedControlReceipt(Guid SessionId, Guid StreamId, Guid ActionKey, long Revision,
    TraceOutcome Outcome, string Reason, bool LocalCancellationVerified)
{
    public string Schema => "suprachat-owned-control/v1";
    public string Authority => "SINGLE_OPERATOR_PROCESS_LOCAL";
    public bool ProviderStopVerified => false;
    public bool CallbackCompletionVerified => false;
}

/// <summary>Bounded local cancellation/fixture effects. Never a durable session or W8 actor authority.</summary>
public sealed class LocalOwnedStreamControls : IDisposable
{
    private readonly object _gate = new();
    private readonly Guid _sessionId;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, OwnedStreamLease> _streams = new();
    private readonly Dictionary<Guid, OwnedControlPlan> _plans = new();
    private readonly Dictionary<Guid, (Guid Plan, long Revision, bool Confirmed, OwnedControlReceipt Receipt)> _outcomes = new();
    private bool _disposed;

    public LocalOwnedStreamControls(Guid sessionId, TimeProvider? clock = null)
    {
        _sessionId = sessionId;
        _clock = clock ?? TimeProvider.System;
    }

    public OwnedStreamLease Register(TraceSurface surface)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (surface is not (TraceSurface.ResponsesSse or TraceSurface.ResponsesWebSocket or TraceSurface.CodexJsonRpc or TraceSurface.Synthetic))
                throw new UnauthorizedAccessException("Owned stream surface refused.");
            if (_streams.Count >= 16) throw new InvalidOperationException("Owned stream capacity reached.");
            var stream = new OwnedStreamLease(this, surface);
            _streams.Add(stream.StreamId, stream);
            return stream;
        }
    }

    public IReadOnlyList<OwnedStreamState> List()
    {
        lock (_gate) return _streams.Values.Select(s => new OwnedStreamState(s.StreamId, s.Surface, s.Revision, s.Token.IsCancellationRequested)).ToArray();
    }

    public OwnedControlPlan Preview(Guid sessionId, Guid streamId, OwnedControlAction action, long expectedRevision, Guid actionKey)
    {
        lock (_gate)
        {
            Authorize(sessionId);
            var now = _clock.GetUtcNow();
            foreach (var key in _plans.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) _plans.Remove(key);
            var reason = !Enum.IsDefined(action) || action is OwnedControlAction.ModifyFirstParty or OwnedControlAction.CapturePayload ? "OPERATION_UNAVAILABLE" :
                !_streams.TryGetValue(streamId, out var stream) ? "STREAM_UNAVAILABLE" :
                stream.Revision != expectedRevision ? "STALE_REVISION" :
                action != OwnedControlAction.Cancel && stream.Surface != TraceSurface.Synthetic ? "SYNTHETIC_ONLY" :
                actionKey == Guid.Empty ? "ACTION_KEY_REQUIRED" : _plans.Count >= 64 ? "PLAN_CAPACITY" : "CONFIRMATION_REQUIRED";
            var plan = new OwnedControlPlan(reason == "CONFIRMATION_REQUIRED" ? Guid.NewGuid() : Guid.Empty, _sessionId,
                streamId, action, expectedRevision, actionKey, now.AddMinutes(2),
                reason == "CONFIRMATION_REQUIRED" ? TraceOutcome.Requested : TraceOutcome.Refused, reason);
            if (plan.PlanId != Guid.Empty) _plans.Add(plan.PlanId, plan);
            return plan;
        }
    }

    public OwnedControlReceipt Apply(Guid sessionId, Guid planId, long expectedRevision, Guid actionKey, bool confirmed)
    {
        lock (_gate)
        {
            Authorize(sessionId);
            if (_outcomes.TryGetValue(actionKey, out var first))
                return first.Plan == planId && first.Revision == expectedRevision && first.Confirmed == confirmed ? first.Receipt :
                    Refused(Guid.Empty, actionKey, "IDEMPOTENCY_CONFLICT");
            if (_outcomes.Count >= 128) return Refused(Guid.Empty, actionKey, "OUTCOME_CAPACITY");
            if (!_plans.TryGetValue(planId, out var plan) || plan.ActionKey != actionKey)
            {
                var unavailable = Refused(Guid.Empty, actionKey, "PLAN_UNAVAILABLE");
                _outcomes.Add(actionKey, (planId, expectedRevision, confirmed, unavailable));
                return unavailable;
            }
            OwnedControlReceipt receipt;
            if (!confirmed) receipt = Refused(plan.StreamId, actionKey, "CONFIRMATION_REQUIRED");
            else if (plan.ExpiresAt <= _clock.GetUtcNow()) receipt = Refused(plan.StreamId, actionKey, "PLAN_EXPIRED");
            else if (!_streams.TryGetValue(plan.StreamId, out var stream) || stream.IsDisposed) receipt = Refused(plan.StreamId, actionKey, "STREAM_UNAVAILABLE");
            else if (plan.ExpectedRevision != expectedRevision || stream.Revision != expectedRevision)
                receipt = Refused(plan.StreamId, actionKey, "STALE_REVISION", stream.Revision);
            else if (stream.Token.IsCancellationRequested) receipt = Refused(plan.StreamId, actionKey, "ALREADY_CANCELLED", stream.Revision);
            else
            {
                // CTS.CancelAsync queues callbacks rather than running provider/user code under this gate.
                // Applied means a local signal/fixture change only; never claim remote compute stopped.
                stream.Apply(plan.Action);
                stream.Revision++;
                receipt = new(_sessionId, stream.StreamId, actionKey, stream.Revision, TraceOutcome.Applied,
                    plan.Action == OwnedControlAction.Cancel ? "LOCAL_CANCEL_SIGNALLED" : "SYNTHETIC_EFFECT_APPLIED",
                    stream.Token.IsCancellationRequested);
            }
            _outcomes.Add(actionKey, (planId, expectedRevision, confirmed, receipt));
            return receipt;
        }
    }

    private OwnedControlReceipt Refused(Guid stream, Guid key, string reason, long revision = 0) =>
        new(_sessionId, stream, key, revision, TraceOutcome.Refused, reason, false);
    private void Authorize(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sessionId != _sessionId) throw new UnauthorizedAccessException("Owned control session refused.");
    }
    internal void Remove(OwnedStreamLease stream) { lock (_gate) _streams.Remove(stream.StreamId); }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            // Disposal of the controller is not operator approval to cancel provider work.
            _plans.Clear();
            _outcomes.Clear();
        }
    }

    public sealed class OwnedStreamLease : IAsyncDisposable
    {
        private readonly LocalOwnedStreamControls _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private Task _callbacks = Task.CompletedTask;
        private int _disposed;
        private int _delay;
        private int _drop;
        private bool _fault;
        private int _replay;
        public Guid StreamId { get; } = Guid.NewGuid();
        public TraceSurface Surface { get; }
        public long Revision { get; internal set; }
        public CancellationToken Token => _cancellation.Token;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal OwnedStreamLease(LocalOwnedStreamControls owner, TraceSurface surface) { _owner = owner; Surface = surface; }
        internal void Apply(OwnedControlAction action)
        {
            switch (action)
            {
                case OwnedControlAction.Cancel:
                    _callbacks = _cancellation.CancelAsync();
                    // Observe callback failures without putting raw exceptions in receipts or changing first outcomes.
                    _ = _callbacks.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                case OwnedControlAction.SyntheticDelay: _delay = 20; break;
                case OwnedControlAction.SyntheticDrop: _drop = 1; break;
                case OwnedControlAction.SyntheticFault: _fault = true; break;
                case OwnedControlAction.SyntheticReplay: _replay = 1; break;
                default: throw new InvalidOperationException("Unsupported owned effect.");
            }
        }
        public async Task<IReadOnlyList<string>> ReadSyntheticFixtureAsync()
        {
            if (Surface != TraceSurface.Synthetic) throw new UnauthorizedAccessException("Synthetic fixture only.");
            int delay, drop, replay; bool fault;
            lock (_owner._gate) { delay = _delay; drop = _drop; replay = _replay; fault = _fault; }
            await Task.Delay(delay, Token);
            if (fault) throw new InvalidOperationException("Synthetic fixture fault.");
            var frames = Enumerable.Range(drop, 4 - drop).Select(i => "synthetic-frame-" + i).ToList();
            if (replay != 0) frames.Add("synthetic-frame-3");
            return frames.AsReadOnly();
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Remove(this);
            try { await _callbacks.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            _cancellation.Dispose();
        }
    }
}
