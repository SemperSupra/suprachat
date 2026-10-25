using SupraChat.Core;

internal static class NativeConsentIdleExpiryChecks
{
    private sealed class VirtualClock : TimeProvider
    {
        private readonly List<VirtualTimer> _timers = new();
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero).AddTicks(Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Unexpected repeating consent timer.");
            var timer = new VirtualTimer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void FireStaleFirst() => _timers[0].FireEvenIfDisposed();

        public void Advance(TimeSpan elapsed)
        {
            Ticks += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }

        private sealed class VirtualTimer : ITimer
        {
            private readonly VirtualClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _due;
            private bool _disposed;

            public VirtualTimer(VirtualClock clock, TimerCallback callback, object? state, TimeSpan due)
            {
                _clock = clock; _callback = callback; _state = state;
                _due = clock.Ticks + due.Ticks;
            }

            public bool Change(TimeSpan due, TimeSpan period)
            {
                if (_disposed || period != Timeout.InfiniteTimeSpan) return false;
                _due = _clock.Ticks + due.Ticks;
                return true;
            }

            public void FireIfDue()
            {
                if (_disposed || _clock.Ticks < _due) return;
                _disposed = true;
                _callback(_state);
            }

            // Simulates an already-queued callback racing disposal and renewal.
            public void FireEvenIfDisposed() => _callback(_state);
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    public static async Task RunAsync()
    {
        using var trace = new TraceNormalizer();
        var clock = new VirtualClock();
        using var consent = new MetadataCaptureConsent(trace, clock);
        var session = trace.CreateSession();
        var view = new object();
        var binding = consent.RegisterNativeSynthetic(session, view);
        if (consent.Confirm(binding, consent.Preview(binding), reviewed: true).Outcome != "ALLOWED")
            throw new InvalidOperationException("Synthetic consent fixture refused.");
        var revoked = consent.GetNativeRevocationTask(binding, view);
        clock.Advance(TimeSpan.FromSeconds(119));
        if (revoked.IsCompleted || !trace.IsCapturing(session))
            throw new InvalidOperationException("Native lease expired before its deadline.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await revoked.WaitAsync(TimeSpan.FromSeconds(1));
        if (trace.IsCapturing(session) || consent.IsNativeSyntheticAdmitted(binding, view) ||
            consent.TryObserve(binding, view,
                new TraceObservation(TraceSurface.Synthetic, TraceAdapter.WebView2,
                    TraceDirection.Ingress, TraceEventKind.Chunk)))
            throw new InvalidOperationException("Native idle expiry retained recorder authority.");
        Console.WriteLine("Native consent idle expiry timer PASS (no UI polling or network callbacks)");
    }
}
