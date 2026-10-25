using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using SupraChat.Core;

namespace SupraChat.WebViewProbe;

internal static class NativeReviewUiChecks
{
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
            return new { actual_review_controls = true, acknowledgement_without_typing = true, expired_review_disabled = true,
                repeated_platform_getter_ready = true, missing_native_view_explained = true, reattached_view_ready = true,
                capture_end_explained = true,
                first_party_live_executed = false };
        }
        finally { review.Close(); }
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
        Require(condition());
    }
}
