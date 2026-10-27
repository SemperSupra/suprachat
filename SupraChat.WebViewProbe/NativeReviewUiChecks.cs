using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using SupraChat.Core;
using System.Text.Json;

namespace SupraChat.WebViewProbe;

internal static class NativeReviewUiChecks
{
    internal static string Phase { get; private set; } = "not-started";
    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Synthetic native review UI qualification failed."); }
    private static T Control<T>(Window window, string id) where T : Avalonia.Controls.Control =>
        window.GetLogicalDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    internal static async Task<object> RunAsync(NativeWebView view)
    {
        Phase = "initial-review";
        // The readback is an observation projection, not an unbounded second ledger.
        var historyContract = new NativeMetadataLifecycleReadback();
        historyContract.Record(NativeMetadataLifecycleReadback.Action.Observed, null);
        historyContract.Record(NativeMetadataLifecycleReadback.Action.Observed, null);
        Require(historyContract.Entries.Count == 1);
        for (var i = 0; i < 40; i++) historyContract.Record(NativeMetadataLifecycleReadback.Action.Opened, null);
        Require(historyContract.Entries.Count == 24);
        Require(!historyContract.ToUiValue().Contains("session_id", StringComparison.OrdinalIgnoreCase));

        // App-side admission must refuse an internally produced history that
        // disagrees with the durable aggregate, not rely solely on a later
        // independent PowerShell validator finding the contradiction.
        var settled = new NativeMetadataStatus(
            new MetadataConsentStatus(Guid.Empty, 0, "STOPPED", false, 0, 0),
            ObserverAttached: false, Attaching: false, NativeEvents: 0, NativeLoss: 0,
            Scope: "SYNTHETIC_LOOPBACK_NATIVE", EndReason: NativeCaptureEndReason.OperatorStop,
            CleanupComplete: true);
        var historyForSave = new NativeMetadataLifecycleReadback();
        historyForSave.Record(NativeMetadataLifecycleReadback.Action.Observed, settled);
        var agreement = new Dictionary<string, object>
        {
            ["schema"] = "suprachat-synthetic-native-metadata-summary/v1",
            ["first_lease_end_reason"] = "OperatorStop",
            ["capture_active"] = false, ["observer_attached"] = false, ["attaching"] = false,
            ["cleanup_complete"] = true, ["enable_completed"] = false, ["disable_completed"] = false
        };
        var upgraded = NativeMetadataSummary.AttachLifecycle(JsonSerializer.SerializeToElement(agreement), historyForSave);
        Require(upgraded.GetProperty("schema").GetString() == "suprachat-synthetic-native-metadata-summary/v2");
        foreach (var (field, contradiction) in new (string Field, object Contradiction)[]
        {
            ("first_lease_end_reason", "Navigation"), ("capture_active", true),
            ("observer_attached", true), ("attaching", true),
            ("cleanup_complete", false), ("enable_completed", true),
            ("disable_completed", true), ("schema", "unsupported/v99")
        })
        {
            var invalid = new Dictionary<string, object>(agreement) { [field] = contradiction };
            var rejected = false;
            try { _ = NativeMetadataSummary.AttachLifecycle(JsonSerializer.SerializeToElement(invalid), historyForSave); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected);
        }
        await using var fixture = new FixtureServer();
        view.Source = fixture.ManualPage("a");
        IntPtr CurrentHandle() => view.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle ? handle.CoreWebView2 : IntPtr.Zero;
        await WaitAsync(() => CurrentHandle() != IntPtr.Zero && CoreWebView2.CreateFromComICoreWebView2(CurrentHandle()).DocumentTitle == "fixture-ready");
        var clock = new Clock();
        var output = Path.Combine(Path.GetTempPath(), "suprachat-review-ui-" + Guid.NewGuid().ToString("N"));
        var review = NativeMetadataReviewWindow.CreateSyntheticQualification(Guid.NewGuid(), CurrentHandle, fixture.Origin, output, clock);
        review.Show((Window)TopLevel.GetTopLevel(view)!);
        try
        {
            var prepare = Control<Button>(review, "Native.Metadata.Prepare");
            var confirm = Control<Button>(review, "Native.Metadata.Confirm");
            var ack = Control<CheckBox>(review, "Native.Metadata.Acknowledge");
            var message = Control<TextBlock>(review, "Native.Metadata.Message");
            var readiness = Control<TextBlock>(review, "Native.Metadata.Readiness");
            var lifecycle = Control<TextBox>(review, "Native.Metadata.LifecycleReadback");
            Require(lifecycle.IsReadOnly && lifecycle.Text!.Contains("suprachat-native-lifecycle-readback/v1", StringComparison.Ordinal));
            Require(!lifecycle.Text!.Contains("session_id", StringComparison.OrdinalIgnoreCase));
            Require(!confirm.IsEnabled && !review.GetLogicalDescendants().OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) == "Native.Metadata.Phrase"));
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => message.Text!.Contains("ACKNOWLEDGEMENT_REQUIRED"));
            Require(!confirm.IsEnabled);
            ack.IsChecked = true;
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => confirm.IsEnabled);
            ack.IsChecked = false; await WaitAsync(() => !confirm.IsEnabled);
            ack.IsChecked = true; await WaitAsync(() => confirm.IsEnabled);
            clock.Seconds = 61; await WaitAsync(() => !confirm.IsEnabled && readiness.Text!.Contains("expired"));
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => confirm.IsEnabled);
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => message.Text!.StartsWith("Metadata capture active"));
            Require(!confirm.IsEnabled);
            Control<Button>(review, "Native.Metadata.Stop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => message.Text!.StartsWith("Capture stopped"));
            Require(lifecycle.Text!.Contains("StopRequested", StringComparison.Ordinal) &&
                lifecycle.Text.Contains("OperatorStop", StringComparison.Ordinal));
            // Exercise the real platform getter after detachment, not a cached handle fixture.
            var parent = (Grid)view.Parent!;
            parent.Children.Remove(view);
            await WaitAsync(() => CurrentHandle() == IntPtr.Zero);
            ack.IsChecked = true; prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => message.Text!.Contains("NATIVE_VIEW_UNAVAILABLE"));
            Require(!confirm.IsEnabled);
            parent.Children.Add(view);
            await WaitAsync(() => CurrentHandle() != IntPtr.Zero && CoreWebView2.CreateFromComICoreWebView2(CurrentHandle()).DocumentTitle == "fixture-ready");
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => confirm.IsEnabled);
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => message.Text!.StartsWith("Metadata capture active"));
            clock.Seconds += 121;
            await WaitAsync(() => message.Text!.StartsWith("Capture expired"));
            Require(lifecycle.Text!.Contains("LeaseExpired", StringComparison.Ordinal));
            foreach (var forbidden in new[] { FixtureServer.Sentinel, "session_id", "actor_id", "https://", "Bearer ", "Authorization:" })
                Require(!lifecycle.Text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
            review.Close(); review.Close();
            var expiredClose = await review.CloseCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Require(expiredClose.ClosedObserved && expiredClose.AggregateSaved && expiredClose.Failure == "NONE" &&
                expiredClose.Status is { EndReason: NativeCaptureEndReason.LeaseExpired, CleanupComplete: true, ObserverAttached: false });
            CheckSummary(output, NativeCaptureEndReason.LeaseExpired);

            // Exercise the actual own-window handlers; never enroll a signed-in profile.
            await CloseCase("active", NativeCaptureEndReason.ReviewClosed);
            await CloseCase("pending", NativeCaptureEndReason.ReviewClosed);
            await CloseCase("prepare-race", NativeCaptureEndReason.OperatorStop);
            await CloseCase("save", NativeCaptureEndReason.SummarySave);
            await CloseCase("prepared", NativeCaptureEndReason.None);
            await CloseCase("off", null);
            await CloseCase("failure", NativeCaptureEndReason.ReviewClosed);
            await OwnerCloseCase(false);
            await OwnerCloseCase(true);
            var emptyOwner = new Window { Title = "SupraChat synthetic owner without review" };
            var emptyClosed = false;
            NativeReviewOwnerLifetime.Attach(emptyOwner, () => null);
            emptyOwner.Closed += (_, _) => emptyClosed = true;
            emptyOwner.Show((Window)TopLevel.GetTopLevel(view)!); emptyOwner.Close();
            Require(emptyClosed);
            return new { actual_review_controls = true, acknowledgement_without_typing = true, expired_review_disabled = true,
                repeated_platform_getter_ready = true, missing_native_view_explained = true, reattached_view_ready = true,
                capture_end_explained = true,
                settled_review_close = true, duplicate_close_one_summary = true, close_preserves_first_reason = true,
                close_during_save = true, close_prepared_or_off = true, close_save_failure_reported = true,
                close_during_enable = true, close_during_prepare = true,
                synthetic_summary_distinct = true,
                owner_waits_for_review_close = true, owner_close_default_off = true, owner_without_review_unblocked = true,
                first_party_live_executed = false };

            async Task CloseCase(string mode, NativeCaptureEndReason? expected)
            {
                Phase = "review-close-" + mode;
                var folder = Path.Combine(Path.GetTempPath(), "suprachat-review-close-" + Guid.NewGuid().ToString("N"));
                if (mode == "failure") File.WriteAllText(folder, "synthetic-output-unavailable");
                var window = NativeMetadataReviewWindow.CreateSyntheticQualification(Guid.NewGuid(), CurrentHandle, fixture.Origin, folder, clock);
                window.Show((Window)TopLevel.GetTopLevel(view)!);
                if (mode != "off")
                {
                    Control<CheckBox>(window, "Native.Metadata.Acknowledge").IsChecked = true;
                    Control<Button>(window, "Native.Metadata.Prepare").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitAsync(() => Control<Button>(window, "Native.Metadata.Confirm").IsEnabled);
                    if (mode != "prepared")
                    {
                        Control<Button>(window, "Native.Metadata.Confirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (mode == "pending") Require(!Control<Button>(window, "Native.Metadata.Prepare").IsEnabled);
                        else await WaitAsync(() => Control<TextBlock>(window, "Native.Metadata.Message").Text!.StartsWith("Metadata capture active"));
                    }
                }
                if (mode == "save") Control<Button>(window, "Native.Metadata.Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (mode == "prepare-race")
                {
                    Control<CheckBox>(window, "Native.Metadata.Acknowledge").IsChecked = true;
                    Control<Button>(window, "Native.Metadata.Prepare").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(!Control<Button>(window, "Native.Metadata.Prepare").IsEnabled);
                }
                window.Close(); window.Close();
                var closed = await window.CloseCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                Require(closed.ClosedObserved);
                if (expected is null) Require(closed.Status is null && !closed.AggregateSaved && !Directory.Exists(folder));
                else
                {
                    Require(closed.Status is { CleanupComplete: true, ObserverAttached: false, Attaching: false } && closed.Status.EndReason == expected);
                    if (mode == "failure") Require(!closed.AggregateSaved && closed.Failure == "SUMMARY_SAVE_FAILED");
                    else { Require(closed.AggregateSaved && closed.Failure == "NONE"); CheckSummary(folder, expected.Value); }
                }
            }
            async Task OwnerCloseCase(bool captureActive)
            {
                Phase = captureActive ? "owner-close-active" : "owner-close-off";
                var folder = Path.Combine(Path.GetTempPath(), "suprachat-owner-close-" + Guid.NewGuid().ToString("N"));
                var owner = new Window { Title = "SupraChat synthetic metadata owner close qualification" };
                var window = NativeMetadataReviewWindow.CreateSyntheticQualification(Guid.NewGuid(), CurrentHandle, fixture.Origin, folder, clock);
                var ownerClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var lifetime = NativeReviewOwnerLifetime.Attach(owner, () => window);
                lifetime.TrackReview(window);
                var replacementRefused = false;
                window.Closed += (_, _) => replacementRefused = lifetime.IsClosing;
                owner.Closed += (_, _) => ownerClosed.TrySetResult(window.CloseCompletion.IsCompletedSuccessfully);
                owner.Show((Window)TopLevel.GetTopLevel(view)!); window.Show(owner);
                if (captureActive)
                {
                    Phase = "owner-active-prepare";
                    Control<CheckBox>(window, "Native.Metadata.Acknowledge").IsChecked = true;
                    Control<Button>(window, "Native.Metadata.Prepare").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitAsync(() => Control<Button>(window, "Native.Metadata.Confirm").IsEnabled);
                    Phase = "owner-active-confirm";
                    Control<Button>(window, "Native.Metadata.Confirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitAsync(() => Control<TextBlock>(window, "Native.Metadata.Message").Text!.StartsWith("Metadata capture active"));
                }
                Phase = captureActive ? "owner-active-close-request" : "owner-off-close-request";
                owner.Close(); owner.Close();
                Require(await ownerClosed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Phase = captureActive ? "owner-active-close-result" : "owner-off-close-result";
                var closed = await window.CloseCompletion;
                Require(closed.ClosedObserved && closed.Failure == "NONE" && replacementRefused);
                if (captureActive)
                {
                    Phase = "owner-active-summary-result";
                    Require(closed.AggregateSaved && closed.Status is { EndReason: NativeCaptureEndReason.ReviewClosed, CleanupComplete: true });
                    CheckSummary(folder, NativeCaptureEndReason.ReviewClosed);
                }
                else Require(!closed.AggregateSaved && closed.Status is null && !Directory.Exists(folder));
            }
        }
        finally { review.Close(); }
    }
    private static void CheckSummary(string directory, NativeCaptureEndReason expected)
    {
        var files = Directory.GetFiles(directory, "*.json"); Require(files.Length == 1);
        var json = File.ReadAllText(files[0]);
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        Require(root.GetProperty("schema").GetString() == "suprachat-synthetic-native-metadata-summary/v2" &&
            root.GetProperty("evidence").GetString() == "SYNTHETIC_LOOPBACK_ONLY" &&
            root.GetProperty("first_lease_end_reason").GetString() == expected.ToString() &&
            root.GetProperty("cleanup_complete").GetBoolean() && !root.GetProperty("capture_active").GetBoolean());
        var readback = root.GetProperty("lifecycle_readback");
        var history = readback.GetProperty("events");
        Require(readback.GetProperty("schema").GetString() == "suprachat-native-lifecycle-readback/v1" &&
            readback.GetProperty("completeness").GetString() == "UNKNOWN" &&
            !readback.GetProperty("is_authoritative_for_consent").GetBoolean() &&
            history.GetArrayLength() is >= 1 and <= 24);
        var final = history[history.GetArrayLength() - 1];
        Require(final.GetProperty("EndReason").GetString() == expected.ToString() &&
            !final.GetProperty("Active").GetBoolean() && !final.GetProperty("ObserverAttached").GetBoolean() &&
            final.GetProperty("CleanupComplete").GetBoolean());
        var previous = -1L;
        foreach (var entry in history.EnumerateArray())
        {
            var elapsed = entry.GetProperty("ElapsedMs").GetInt64();
            Require(elapsed >= 0 && elapsed >= previous);
            previous = elapsed;
        }
        foreach (var forbidden in new[] { FixtureServer.Sentinel, "http://", "https://", "Bearer ", "Cookie:", "Authorization:" }) Require(!json.Contains(forbidden));
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
        Require(condition());
    }
}
