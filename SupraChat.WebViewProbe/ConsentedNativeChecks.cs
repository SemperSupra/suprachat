using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using SupraChat.Core;

namespace SupraChat.WebViewProbe;

internal static class ConsentedNativeChecks
{
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Consented native qualification failed."); }
    internal static async Task<object> RunAsync(NativeWebView[] views)
    {
        await using var fixture = new FixtureServer();
        var cores = new CoreWebView2[2];
        var handles = new IntPtr[2];
        for (var i = 0; i < 2; i++)
        {
            handles[i] = ((IWindowsWebView2PlatformHandle)views[i].TryGetPlatformHandle()!).CoreWebView2;
            cores[i] = CoreWebView2.CreateFromComICoreWebView2(handles[i]);
            views[i].Navigate(fixture.ManualPage(i == 0 ? "a" : "b"));
        }
        await WaitAsync(() => cores.All(c => c.DocumentTitle == "fixture-ready"));
        var ledger = new SharedSessionLedger();
        var clock = new ManualClock();
        var idA = Guid.ParseExact(ledger.Open("native-consent-open-a", "automation", "fixture-a").Session!.Id, "N");
        var idB = Guid.ParseExact(ledger.Open("native-consent-open-b", "agent", "fixture-b").Session!.Id, "N");
        var liveHandleA = handles[0];
        await using var a = WindowsConsentedMetadataSession.PrepareSynthetic(handles[0], idA, fixture.Origin, () => liveHandleA, clock);
        await using var b = WindowsConsentedMetadataSession.PrepareSynthetic(handles[1], idB, fixture.Origin, () => handles[1], clock);
        Require(!a.Status().ObserverAttached && !a.Status().Consent.CaptureActive && a.Read().Events.Count == 0);
        var missing = a.Preview(); Require(!await a.ConfirmAsync(missing, false));
        Require(!a.Status().ObserverAttached && a.Read().Events.Count == 0);
        var planA = a.Preview(); Require(await a.ConfirmAsync(planA, true));
        var planB = b.Preview(); Require(await b.ConfirmAsync(planB, true));
        await cores[0].ExecuteScriptAsync("window.runFixture(); void 0");
        await cores[1].ExecuteScriptAsync("window.runFixture(); void 0");
        await WaitAsync(() => cores.All(c => c.DocumentTitle == "fixture-complete-observed"));
        await Task.Delay(250);
        Require(a.Read().Events.Count > 5 && b.Read().Events.Count > 5);
        Require(a.Read().Events.All(e => e.SessionId == idA && e.ProviderSurface == TraceSurface.Synthetic));
        Require(b.Read().Events.All(e => e.SessionId == idB && e.ProviderSurface == TraceSurface.Synthetic));
        Require(!System.Text.Json.JsonSerializer.Serialize(a.Read(), TraceNormalizer.JsonOptions).Contains(FixtureServer.Sentinel));
        var before = a.Read().Events.Count;
        a.Stop(); Require(!await a.ConfirmAsync(planA, true));
        Require(!a.Status().ObserverAttached && !a.Status().Consent.CaptureActive);
        await a.StopForSummaryAsync();
        Require(a.Status().EndReason == NativeCaptureEndReason.OperatorStop && a.Status().CleanupComplete && a.Status().DisableCompleted);
        Require(a.Status().Diagnostics!.TotalIncidents == 0 && a.Read().CaptureGap);
        // No Status/Read, UI watchdog, or new fixture requests may cause the following expiry.
        b.PauseSyntheticWatchdogForQualification();
        var retiredTimer = clock.TimerCount - 1;
        clock.Advance(TimeSpan.FromSeconds(119));
        Require(b.PeekSyntheticLifecycleWithoutRefresh() is { RecorderCapturing: true, ObserverAttached: true });
        b.Stop();
        var renewedB = b.Preview(); Require(await b.ConfirmAsync(renewedB, true));
        var idleNetworkEvents = b.PeekSyntheticLifecycleWithoutRefresh().NetworkEvents;
        clock.FireQueuedTimerEvenIfDisposed(retiredTimer);
        await Task.Delay(100);
        Require(b.PeekSyntheticLifecycleWithoutRefresh() is { RecorderCapturing: true, ObserverAttached: true });
        // An early callback reschedules its own timer instead of ending the current lease.
        var timerChanges = clock.TimerChanges;
        clock.FireQueuedTimerEvenIfDisposed(clock.TimerCount - 1);
        Require(clock.TimerChanges == timerChanges + 1 && b.PeekSyntheticLifecycleWithoutRefresh().RecorderCapturing);
        clock.Advance(TimeSpan.FromSeconds(119));
        Require(b.PeekSyntheticLifecycleWithoutRefresh() is { RecorderCapturing: true, ObserverAttached: true });
        // Also deliver the retired callback long after its old deadline, just before the new one.
        clock.FireQueuedTimerEvenIfDisposed(retiredTimer);
        await Task.Delay(100);
        Require(b.PeekSyntheticLifecycleWithoutRefresh() is { RecorderCapturing: true, ObserverAttached: true });
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(!b.PeekSyntheticLifecycleWithoutRefresh().RecorderCapturing); // Timer, before yielding to any UI/SDK callback.
        await Task.Delay(100);
        var expiredWithoutPolling = b.PeekSyntheticLifecycleWithoutRefresh();
        Require(!expiredWithoutPolling.ObserverAttached && expiredWithoutPolling.NetworkEvents == idleNetworkEvents);
        // Ordinary status/summary APIs are allowed only after independent expiry observations pass.
        await b.StopForSummaryAsync();
        Require(b.Status().EndReason == NativeCaptureEndReason.LeaseExpired && b.Status().CleanupComplete);
        Require(!await b.ConfirmAsync(planB, true) && !await b.ConfirmAsync(renewedB, true));
        Require(a.Read().Events.Count == before);
        await b.DisposeAsync();
        await using var race = WindowsConsentedMetadataSession.PrepareSynthetic(handles[1], idB, fixture.Origin, () => handles[1], clock);
        var stoppedPending = race.Preview();
        var pendingStop = race.ConfirmAsync(stoppedPending, true);
        Require(!pendingStop.IsCompleted && race.Status().Attaching && race.Status().ObserverAttached);
        race.Stop(); Require(!await pendingStop && !race.Status().ObserverAttached);
        // A fresh owner guarantees the next pending window is Network.enable, not previous disposal.
        await race.DisposeAsync();
        await using var expiredRace = WindowsConsentedMetadataSession.PrepareSynthetic(handles[1], idB, fixture.Origin, () => handles[1], clock);
        var expiredPending = expiredRace.Preview(); var pendingExpiry = expiredRace.ConfirmAsync(expiredPending, true);
        Require(!pendingExpiry.IsCompleted && expiredRace.Status().Attaching && expiredRace.Status().ObserverAttached);
        clock.Advance(TimeSpan.FromSeconds(121));
        Require(!await pendingExpiry && !expiredRace.Status().ObserverAttached && !expiredRace.Status().Consent.CaptureActive);
        var replacement = a.Preview(); Require(await a.ConfirmAsync(replacement, true));
        liveHandleA = handles[1]; // A distinct, real CoreWebView2 identity cannot inherit the old view's lease.
        await Task.Delay(300);
        Require(!a.Status().ObserverAttached && !a.Status().Consent.CaptureActive && a.Status().Consent.ViewRevision > 0);
        liveHandleA = handles[0];
        var navigation = a.Preview(); Require(await a.ConfirmAsync(navigation, true));
        views[0].Navigate(new Uri("about:blank"));
        await WaitAsync(() => !a.Status().ObserverAttached);
        Require(!a.Status().Consent.CaptureActive && !await a.ConfirmAsync(navigation, true));
        await a.StopForSummaryAsync();
        Require(a.Status().EndReason == NativeCaptureEndReason.Navigation && a.Status().CleanupComplete);
        return new { gateway_native_capture = true, concurrent_sessions = 2, off_before_review = true,
            no_ack_no_observer = true, historical_confirmation_refused = true, idle_expiry_detached = true,
            stop_during_pending_enable_refused = true, expiry_during_pending_enable_refused = true,
            native_view_replacement_revoked = true, blank_navigation_revoked = true, sentinel_excluded = true,
            intentional_stop_not_loss = true, first_end_reason_preserved = true, cleanup_settled_before_summary = true,
            idle_expiry_without_polling = true, retired_timer_preserved_replacement = true,
            early_timer_rescheduled = true, renewed_lease_deadline_respected = true,
            first_party_live_executed = false };
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
        Require(condition());
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = new();
        internal int TimerCount => _timers.Count;
        internal int TimerChanges;
        internal void FireQueuedTimerEvenIfDisposed(int index) => _timers[index].FireEvenIfDisposed();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; }
        public void Advance(TimeSpan amount)
        { _ticks += amount.Ticks; foreach (var timer in _timers.ToArray()) timer.Fire(); }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due; private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { if (_disposed) return false; clock.TimerChanges++; _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks; return true; }
            public void Fire() { if (!_disposed && clock._ticks >= _due) { _due = long.MaxValue; callback(state); } }
            public void FireEvenIfDisposed() => callback(state);
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
