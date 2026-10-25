using SupraChat;
using SupraChat.Core;

internal static class NativeConsentChecks
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Native consent contract failed."); }
    public static async Task RunAsync()
    {
        NativeDiagnosticChecks.Run();
        NativeMetadataOriginPolicy.Validate(new Uri("https://chatgpt.com/"), TraceSurface.ChatGptWebView);
        foreach (var uri in new[] { "http://chatgpt.com/", "https://chatgpt.com:444/", "https://chatgpt.com.evil/",
            "https://user@chatgpt.com/", "https://chatgpt.com/path", "https://chatgpt.com/?key=x", "https://chatgpt.com/#fragment", "https://www.chatgpt.com/", "about:blank" })
        {
            var refused = false;
            try { NativeMetadataOriginPolicy.Validate(new Uri(uri), TraceSurface.ChatGptWebView); } catch (UnauthorizedAccessException) { refused = true; }
            Require(refused);
        }
        var legacyRefused = false;
        try { NativeMetadataOriginPolicy.Validate(new Uri("https://chatgpt.com/"), TraceSurface.Synthetic); } catch (UnauthorizedAccessException) { legacyRefused = true; }
        Require(legacyRefused);
        using var trace = new TraceNormalizer();
        var clock = new Clock();
        using var consent = new MetadataCaptureConsent(trace, clock);
        var cap = trace.CreateSession(); var other = trace.CreateSession(); var view = new object();
        var binding = consent.RegisterNative(cap, view, firstParty: true);
        var first = consent.Preview(binding, MetadataConsentScope.FirstPartyMetadata);
        Require(first.Review.Available && first.Review.Reason == "FIRST_PARTY_METADATA_REVIEW");
        var sink = consent.NativeSink(binding, view, first);
        var sample = new TraceObservation(TraceSurface.ChatGptWebView, TraceAdapter.WebView2, TraceDirection.Ingress, TraceEventKind.Response,
            Origin: "https://chatgpt.com/", RequestId: "synthetic-native-consent-private", Status: 200);
        Require(!sink.TryObserve(cap, sample));
        consent.Confirm(binding, first, true);
        Require(consent.NativeLeaseActive(binding, view, first) && sink.TryObserve(cap, sample));
        Require(!sink.TryObserve(other, sample) && !consent.TryObserve(binding, view, sample));
        Require(!sink.TryObserve(cap, sample with { ProviderSurface = TraceSurface.Synthetic }));
        consent.Stop(binding);
        Require(consent.Confirm(binding, first, true).Outcome == "ALLOWED" && !consent.NativeLeaseActive(binding, view, first));
        var second = consent.Preview(binding, MetadataConsentScope.FirstPartyMetadata);
        consent.Confirm(binding, second, true);
        Require(!sink.TryObserve(cap, sample)); // An earlier producer cannot borrow the renewed lease.
        Require(consent.NativeSink(binding, view, second).TryObserve(cap, sample));
        clock.Seconds = 121;
        Require(consent.NativeLeaseRemaining(binding, view, second) == TimeSpan.Zero && !trace.IsCapturing(cap));
        Require(!System.Text.Json.JsonSerializer.Serialize(trace.Read(cap), TraceNormalizer.JsonOptions).Contains("synthetic-native-consent-private"));
        var page = trace.Read(cap);
        var diagnostic = page.Events[0] with { EventKind = TraceEventKind.CaptureGap, Direction = TraceDirection.Local, RequestId = "private-summary-sentinel" };
        var status = new NativeMetadataStatus(consent.Status(binding), false, false, 0, 1, "SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN");
        var summary = NativeMetadataSummary.Create(status, page with { Events = new[] { diagnostic } }, "test-build-hash");
        Require(summary.GetProperty("evidence").GetString() == "NO_FIRST_PARTY_OBSERVATIONS" &&
            !summary.GetRawText().Contains("private-summary-sentinel"));

        var fence = new ReviewOperationFence();
        var generation = fence.TryBegin()!.Value;
        Require(fence.TryBegin() is null && fence.Current(generation));
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createdAfterClose = 0;
        var pending = Task.Run(async () => { await released.Task; if (fence.Current(generation)) Interlocked.Increment(ref createdAfterClose); });
        fence.Close(); released.SetResult(); await pending;
        fence.End(); Require(createdAfterClose == 0 && fence.TryBegin() is null);
        var cancel = new ReviewOperationFence(); var old = cancel.TryBegin()!.Value;
        cancel.Cancel(); Require(!cancel.Current(old) && cancel.TryBegin() is null);
        cancel.End(); Require(cancel.TryBegin() is { } fresh && cancel.Current(fresh));
        var registration = new NativeNetworkRegistration();
        var cleanup = registration.WaitForEnableAsync(); // Stop reenters before the COM invocation returns a task.
        Require(!cleanup.IsCompleted);
        var enable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registration.Publish(enable.Task); registration.Complete();
        Require(!cleanup.IsCompleted);
        enable.SetResult(); await cleanup.WaitAsync(TimeSpan.FromSeconds(3));
        var aborted = new NativeNetworkRegistration(); var abortedCleanup = aborted.WaitForEnableAsync();
        Require(!abortedCleanup.IsCompleted); aborted.Complete(); await abortedCleanup.WaitAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine("Native consent checks PASS (synthetic unit observations only; origin, plan-bound sink, expiry and review lifecycle)");
    }
}
