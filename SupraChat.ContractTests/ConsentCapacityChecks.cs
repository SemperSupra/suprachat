using SupraChat.Core;

// Characterizes the current retention policy, not an approved pruning design.
// A future policy change must deliberately revise these checks and preserve replay.
internal static class ConsentCapacityChecks
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    public static void Run()
    {
        foreach (var ending in new[] { "cancelled", "confirmed-stopped", "expired" })
            Characterize(ending);
        ConcurrentBoundary();
        Console.WriteLine("Consent capacity characterization PASS (64 historical plans; 65th refused; replay/isolation/disposal; synthetic only)");
    }

    private static void Characterize(string ending)
    {
        using var trace = new TraceNormalizer();
        var clock = new Clock();
        using var owner = new MetadataCaptureConsent(trace, clock);
        var view = new object();
        var binding = owner.Register(trace.CreateSession(), view);
        var otherBinding = owner.Register(trace.CreateSession(), new object());
        var plans = new List<MetadataConsentPlan>();
        var outcomes = new List<MetadataConsentResult>();
        for (var i = 0; i < 64; i++)
        {
            var plan = owner.Preview(binding);
            plans.Add(plan);
            if (ending == "cancelled") owner.CancelPreview(plan);
            if (ending != "expired")
            {
                var result = owner.Confirm(binding, plan, reviewed: true);
                Require(result.Outcome == (ending == "cancelled" ? "REFUSED" : "ALLOWED"), "first outcome");
                outcomes.Add(result);
                if (ending == "confirmed-stopped") owner.Stop(binding);
            }
        }
        if (ending == "expired")
        {
            clock.Advance(60);
            foreach (var plan in plans)
            {
                Require(!owner.IsPending(binding, plan), "expired pending flag");
                var result = owner.Confirm(binding, plan, reviewed: true);
                Require(result.Reason == "REVIEW_EXPIRED", "expired outcome");
                outcomes.Add(result);
            }
        }
        RefusesCapacity(() => owner.Preview(binding));
        // The cap belongs to the owner, not a single binding/view.
        RefusesCapacity(() => owner.Preview(otherBinding));
        owner.Stop(binding);
        var replacementView = new object();
        owner.ReplaceView(binding, view, replacementView);
        RefusesCapacity(() => owner.Preview(binding));
        for (var i = 0; i < plans.Count; i++)
        {
            Require(ReferenceEquals(outcomes[i], owner.Confirm(binding, plans[i], reviewed: true)), "immutable replay");
            owner.CancelPreview(plans[i]);
            Require(ReferenceEquals(outcomes[i], owner.Confirm(binding, plans[i], reviewed: false)), "cancel cannot rewrite first outcome");
        }
        Require(!owner.Status(binding).CaptureActive && !owner.Status(otherBinding).CaptureActive, "historical replay re-enabled capture");
        using (var independent = new MetadataCaptureConsent(trace, clock))
        {
            var independentBinding = independent.Register(trace.CreateSession(), new object());
            var fresh = independent.Preview(independentBinding);
            Require(independent.IsPending(independentBinding, fresh), "owner isolation");
        }
        owner.Dispose();
        try { owner.Preview(binding); throw new InvalidOperationException("disposed owner accepted preview"); }
        catch (ObjectDisposedException) { }
        using var renewed = new MetadataCaptureConsent(trace, clock);
        var renewedBinding = renewed.Register(trace.CreateSession(), new object());
        try { renewed.Confirm(renewedBinding, plans[0], true); throw new InvalidOperationException("foreign plan accepted"); }
        catch (UnauthorizedAccessException ex) when (ex.Message == "CONSENT_PLAN_REFUSED") { }
        Require(renewed.IsPending(renewedBinding, renewed.Preview(renewedBinding)), "new owner remains usable");
    }

    private static void RefusesCapacity(Func<MetadataConsentPlan> operation)
    {
        try { operation(); throw new InvalidOperationException("65th consent plan accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "CONSENT_REVIEW_CAPACITY_REACHED") { }
    }
    private static void ConcurrentBoundary()
    {
        using var trace = new TraceNormalizer();
        using var owner = new MetadataCaptureConsent(trace, new Clock());
        var a = owner.Register(trace.CreateSession(), new object());
        var b = owner.Register(trace.CreateSession(), new object());
        for (var i = 0; i < 63; i++) owner.CancelPreview(owner.Preview(a));
        var results = Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            try { owner.Preview(i % 2 == 0 ? a : b); return true; }
            catch (InvalidOperationException ex) when (ex.Message == "CONSENT_REVIEW_CAPACITY_REACHED") { return false; }
        }))).GetAwaiter().GetResult();
        Require(results.Count(x => x) == 1, "concurrent 63-to-64 admission");
        RefusesCapacity(() => owner.Preview(a));
        RefusesCapacity(() => owner.Preview(b));
        Require(!owner.Status(a).CaptureActive && !owner.Status(b).CaptureActive, "preview activated capture");
    }
    private static void Require(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException("Consent capacity characterization failed: " + reason);
    }
}
