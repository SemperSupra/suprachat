using SupraChat.Core;

internal static class MetadataConsentChecks
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Advance(int seconds) { Ticks += TimeSpan.FromSeconds(seconds).Ticks; Utc = Utc.AddSeconds(seconds); }
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Metadata consent contract failed."); }
    private static readonly TraceObservation Sample = new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Local,
        TraceEventKind.Request, RequestId: "synthetic-private-consent-sentinel");
    public static async Task RunAsync()
    {
        using var trace = new TraceNormalizer(capacityPerSession: 2);
        var clock = new Clock();
        using var consent = new MetadataCaptureConsent(trace, clock);
        var ca = trace.CreateSession(); var cb = trace.CreateSession();
        var va = new object(); var vb = new object();
        var a = consent.Register(ca, va); var b = consent.Register(cb, vb);
        Require(!consent.Status(a).CaptureActive && !consent.TryObserve(a, va, Sample) && trace.Read(ca).Events.Count == 0);
        var rejected = consent.Preview(a);
        Require(consent.Confirm(a, rejected, reviewed: false).Reason == "REVIEW_NOT_ACKNOWLEDGED");
        Require(ReferenceEquals(consent.Confirm(a, rejected, true), consent.Confirm(a, rejected, false)));
        foreach (var scope in new[] { MetadataConsentScope.FirstPartyMetadata, MetadataConsentScope.Payload, (MetadataConsentScope)99 })
        {
            var unavailable = consent.Preview(a, scope);
            Require(!unavailable.Review.Available && consent.Confirm(a, unavailable, true).Reason == "CAPTURE_SCOPE_UNAVAILABLE");
        }
        var pa = consent.Preview(a);
        Require(consent.IsPending(a, pa) && !consent.IsPending(b, pa));
        Require(consent.Confirm(b, pa, true).Reason == "WRONG_SESSION_OR_VIEW");
        var allowed = consent.Confirm(a, pa, true);
        Require(allowed.Outcome == "ALLOWED" && consent.Status(a).CaptureActive && !consent.Status(b).CaptureActive);
        Require(consent.Confirm(b, pa, true).Outcome == "REFUSED");
        Require(!consent.TryObserve(a, vb, Sample));
        Require(!consent.TryObserve(a, va, Sample with { ProviderSurface = TraceSurface.ChatGptWebView, Adapter = TraceAdapter.WebView2 }));
        Require(consent.TryObserve(a, va, Sample));
        var pb = consent.Preview(b); consent.Confirm(b, pb, true);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => consent.Confirm(a, pa, true))));
        clock.Advance(100);
        Require(ReferenceEquals(allowed, consent.Confirm(a, pa, true)));
        clock.Advance(20);
        Require(!consent.TryObserve(a, va, Sample) && consent.Status(a).State == "EXPIRED");
        Require(!consent.TryObserve(b, vb, Sample));
        Require(consent.Confirm(a, pa, true).Outcome == "ALLOWED" && !consent.Status(a).CaptureActive);
        var expired = consent.Preview(a); clock.Advance(60);
        Require(!consent.IsPending(a, expired));
        Require(consent.Confirm(a, expired, true).Reason == "REVIEW_EXPIRED");
        var cancelled = consent.Preview(a); consent.CancelPreview(cancelled);
        Require(consent.Confirm(a, cancelled, true).Reason == "REVIEW_CANCELLED");
        var stale = consent.Preview(a);
        var nextView = new object(); consent.ReplaceView(a, va, nextView);
        Require(consent.Confirm(a, stale, true).Reason == "VIEW_CHANGED");
        var fresh = consent.Preview(a); consent.Confirm(a, fresh, true);
        Require(!consent.TryObserve(a, va, Sample) && consent.TryObserve(a, nextView, Sample));
        for (var i = 0; i < 5; i++) Require(consent.TryObserve(a, nextView, Sample));
        Require(consent.Status(a).RetainedEvents == 2 && trace.Read(ca).CaptureGap);
        var stopPreview = consent.Preview(a); consent.Stop(a);
        Require(consent.Confirm(a, stopPreview, true).Reason == "REVIEW_INVALIDATED" && !consent.TryObserve(a, nextView, Sample));
        var independent = consent.Preview(b); consent.Confirm(b, independent, true);
        consent.Stop(a);
        Require(consent.Status(b).CaptureActive && consent.TryObserve(b, vb, Sample));
        trace.SetCapture(cb, TraceCaptureMode.Off);
        Require(consent.Status(b).State == "RECORDER_STOPPED" && !consent.Status(b).CaptureActive);
        consent.Stop(b);
        var rollback = consent.Preview(a); clock.Utc = clock.Utc.AddYears(-1); clock.Advance(60);
        Require(consent.Confirm(a, rollback, true).Reason == "REVIEW_EXPIRED");
        Require(!System.Text.Json.JsonSerializer.Serialize(trace.Read(ca), TraceNormalizer.JsonOptions).Contains("synthetic-private-consent-sentinel"));
        using var foreignTrace = new TraceNormalizer();
        using var foreignConsent = new MetadataCaptureConsent(foreignTrace);
        var foreign = foreignConsent.Register(foreignTrace.CreateSession(ca.SessionId), new object());
        var refused = false;
        try { consent.Status(foreign); } catch (UnauthorizedAccessException) { refused = true; }
        Require(refused && !consent.TryObserve(foreign, nextView, Sample));
        var foreignPlan = foreignConsent.Preview(foreign);
        refused = false;
        try { consent.Confirm(a, foreignPlan, true); } catch (UnauthorizedAccessException) { refused = true; }
        Require(refused && !foreignConsent.Status(foreign).CaptureActive);
        refused = false;
        for (var i = 0; i < 64; i++)
            try { consent.Preview(a); } catch (InvalidOperationException) { refused = true; break; }
        Require(refused);
        consent.Dispose();
        Require(!trace.IsCapturing(ca) && !consent.TryObserve(a, nextView, Sample));
        using var broken = new TraceNormalizer();
        using var brokenConsent = new MetadataCaptureConsent(broken);
        var brokenView = new object();
        var brokenBinding = brokenConsent.Register(broken.CreateSession(), brokenView);
        brokenConsent.Confirm(brokenBinding, brokenConsent.Preview(brokenBinding), true);
        broken.Dispose();
        Require(!brokenConsent.TryObserve(brokenBinding, brokenView, Sample));
        // The default fixture binding cannot impersonate a native WebView producer.
        using var nativeTrace = new TraceNormalizer();
        using var nativeConsent = new MetadataCaptureConsent(nativeTrace);
        var ordinaryCap = nativeTrace.CreateSession();
        var nativeCap = nativeTrace.CreateSession();
        var ordinaryView = new object();
        var nativeView = new object();
        var ordinary = nativeConsent.Register(ordinaryCap, ordinaryView);
        var native = nativeConsent.RegisterNativeSynthetic(nativeCap, nativeView);
        var browserEvent = new TraceObservation(TraceSurface.Synthetic, TraceAdapter.WebView2,
            TraceDirection.Ingress, TraceEventKind.Response, "http://127.0.0.1/fixture");
        Require(!nativeConsent.IsNativeSyntheticAdmitted(native, nativeView));
        Require(!nativeConsent.TryObserve(native, nativeView, browserEvent));
        nativeConsent.Confirm(ordinary, nativeConsent.Preview(ordinary), true);
        nativeConsent.Confirm(native, nativeConsent.Preview(native), true);
        Require(!nativeConsent.IsNativeSyntheticAdmitted(ordinary, ordinaryView));
        Require(!nativeConsent.TryObserve(ordinary, ordinaryView, browserEvent));
        Require(!nativeConsent.IsNativeSyntheticAdmitted(native, ordinaryView));
        Require(nativeConsent.IsNativeSyntheticAdmitted(native, nativeView));
        var revokedOnStop = nativeConsent.GetNativeRevocationTask(native, nativeView);
        Require(!revokedOnStop.IsCompleted);
        Require(nativeConsent.TryObserve(native, nativeView, browserEvent));
        Require(!nativeConsent.TryObserve(native, nativeView,
            browserEvent with { ProviderSurface = TraceSurface.ChatGptWebView }));
        Require(!nativeConsent.TryObserve(native, nativeView,
            browserEvent with { ProviderSurface = TraceSurface.ResponsesSse }));
        Require(nativeTrace.Read(nativeCap).Events.Count == 1 &&
            nativeTrace.Read(ordinaryCap).Events.Count == 0);
        nativeConsent.Stop(native);
        await revokedOnStop.WaitAsync(TimeSpan.FromSeconds(1));
        Require(!nativeConsent.IsNativeSyntheticAdmitted(native, nativeView) &&
            !nativeConsent.TryObserve(native, nativeView, browserEvent));
        Require(nativeTrace.Read(nativeCap).Events.Count == 1);
        // Native consent revocation is an idle signal, independent of future traffic.
        using var revocationTrace = new TraceNormalizer();
        var revocationClock = new Clock();
        using var revocation = new MetadataCaptureConsent(revocationTrace, revocationClock);
        var revocationView = new object();
        var revocationBinding = revocation.RegisterNativeSynthetic(revocationTrace.CreateSession(), revocationView);
        revocation.Confirm(revocationBinding, revocation.Preview(revocationBinding), true);
        var firstRevocation = revocation.GetNativeRevocationTask(revocationBinding, revocationView);
        Require(!firstRevocation.IsCompleted);
        revocation.Confirm(revocationBinding, revocation.Preview(revocationBinding), true);
        await firstRevocation.WaitAsync(TimeSpan.FromSeconds(1));
        var secondRevocation = revocation.GetNativeRevocationTask(revocationBinding, revocationView);
        revocationClock.Advance(121);
        Require(revocation.Status(revocationBinding).State == "EXPIRED");
        await secondRevocation.WaitAsync(TimeSpan.FromSeconds(1));
        revocation.Confirm(revocationBinding, revocation.Preview(revocationBinding), true);
        var viewRevocation = revocation.GetNativeRevocationTask(revocationBinding, revocationView);
        var newRevocationView = new object();
        revocation.ReplaceView(revocationBinding, revocationView, newRevocationView);
        await viewRevocation.WaitAsync(TimeSpan.FromSeconds(1));
        Require(!revocation.IsNativeSyntheticAdmitted(revocationBinding, revocationView));
        revocation.Confirm(revocationBinding, revocation.Preview(revocationBinding), true);
        var disposalRevocation = revocation.GetNativeRevocationTask(revocationBinding, newRevocationView);
        revocation.Dispose();
        await disposalRevocation.WaitAsync(TimeSpan.FromSeconds(1));
        await NativeConsentIdleExpiryChecks.RunAsync();
        Console.WriteLine("Metadata consent checks PASS (review, scope, session/view binding, expiry, stop, replay, bounded retention)");
    }
}
