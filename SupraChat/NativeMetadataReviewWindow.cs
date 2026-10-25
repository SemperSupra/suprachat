using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SupraChat.Core;

namespace SupraChat;

/// <summary>Local interactive experiment only. Never constructed by public synthetic qualification.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeMetadataReviewWindow : Window
{
    private WindowsConsentedMetadataSession? _capture;
    private MetadataConsentPlan? _plan;
    private readonly ReviewOperationFence _operations = new();
    private bool _closed, _busy;
    private readonly TextBox _phrase = Named(new TextBox(), "Native.Metadata.Phrase", "Type ALLOW METADATA to opt in");
    private readonly CheckBox _ack = Named(new CheckBox { Content = "I reviewed this window and metadata-only scope" }, "Native.Metadata.Acknowledge", "Acknowledge the local metadata experiment");
    private readonly Button _review = Named(new Button { Content = "Prepare this window's review" }, "Native.Metadata.Prepare", "Prepare metadata review");
    private readonly Button _confirm = Named(new Button { Content = "Confirm two-minute metadata capture", IsEnabled = false }, "Native.Metadata.Confirm", "Confirm reviewed first-party metadata capture");
    private readonly TextBlock _message = Named(new TextBlock { Text = "Capture OFF. No network observer is attached.", TextWrapping = TextWrapping.Wrap }, "Native.Metadata.Message", "Native metadata experiment status");
    private readonly TextBox _status = Named(new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 160 }, "Native.Metadata.Status", "Sanitized metadata capture state");
    private readonly Button _save = Named(new Button { Content = "Save sanitized summary…" }, "Native.Metadata.Save", "Save aggregate metadata experiment summary");

    internal NativeMetadataReviewWindow(Guid sessionId, Func<IntPtr> currentHandle)
    {
        Title = "Review ChatGPT metadata experiment"; Width = 690; Height = 650;
        AutomationProperties.SetAutomationId(this, "Native.Metadata.Window");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Local metadata-only experiment", FontSize = 24 });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "Scope: this browser window at chatgpt.com, for at most two minutes. Only origin category, event timing, direction, " +
            "status, byte counts and pseudonymous correlation are retained in memory (up to 256 events). " +
            "CDP event JSON, including off-origin requests, reaches this process before origin filtering; headers, cookies, prompts, responses and frame payloads are not retained or exported. " +
            "Stop, navigation, view replacement or closing this window ends capture. Earlier metadata stays in memory until close. " +
            "No provider action, automatic upload, conversation binding or verified-human claim is enabled. " +
            "Use only after source review and your explicit opt-in for this test. Keep the ChatGPT tab open." });
        foreach (var control in new Control[] { _ack, _phrase, _review, _confirm }) panel.Children.Add(control);
        var stop = Named(new Button { Content = "Stop capture" }, "Native.Metadata.Stop", "Stop first-party metadata capture");
        panel.Children.Add(stop); panel.Children.Add(_save); panel.Children.Add(_message); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel };
        _review.Click += async (_, _) => await RunOperation(async generation =>
        {
            _confirm.IsEnabled = false;
            var prior = _capture; _capture = null; _plan = null;
            if (prior is not null) await SafeDispose(prior);
            if (!_operations.Current(generation)) return;
            var handle = currentHandle();
            var optIn = LocalFirstPartyMetadataOptIn.FromExplicitReview(sessionId, handle, _ack.IsChecked == true, _phrase.Text ?? "");
            var prepared = WindowsConsentedMetadataSession.PrepareFirstParty(handle, sessionId, currentHandle, optIn);
            if (!_operations.Current(generation)) { await SafeDispose(prepared); return; }
            _capture = prepared; _plan = prepared.Preview();
            _message.Text = "Review ready for this local browser session. Confirm within one minute. Capture remains OFF.";
        });
        _confirm.Click += async (_, _) => await RunOperation(async generation =>
        {
            _confirm.IsEnabled = false;
            if (_capture is not { } capture || _plan is not { } plan) return;
            _plan = null;
            var reviewed = _ack.IsChecked == true && _phrase.Text == "ALLOW METADATA";
            _ack.IsChecked = false; _phrase.Text = "";
            var attached = await capture.ConfirmAsync(plan, reviewed);
            if (!_operations.Current(generation)) return;
            _message.Text = attached ? "Metadata capture active. Normal browser activity remains authoritative; no provider action was requested." :
                "Observer did not start or consent expired. Prepare a new review to try again.";
        });
        stop.Click += (_, _) =>
        {
            _operations.Cancel(); _plan = null; SafeStop(_capture); _ack.IsChecked = false; _phrase.Text = "";
            _message.Text = "Capture stopped. Earlier metadata remains until this review window closes."; Refresh();
        };
        _save.Click += async (_, _) => await RunOperation(async generation =>
        {
                if (_capture is not { } capture) { _message.Text = "No experiment to summarize."; return; }
                _plan = null; capture.Stop();
                var summary = BuildSummary(capture); // No event arrays/aliases survive the file-picker await.
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                { Title = "Save a new sanitized metadata summary", SuggestedFileName = "suprachat-metadata-summary-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json" });
                if (!_operations.Current(generation)) return;
                if (file?.TryGetLocalPath() is not { } path) return;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(output, summary, new JsonSerializerOptions { WriteIndented = true });
                _message.Text = "Stopped and saved an aggregate summary. No event IDs, URLs, titles or payloads were exported.";
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => { _closed = true; _operations.Close(); timer.Stop(); _plan = null;
            var capture = _capture; _capture = null; if (capture is not null) { SafeStop(capture); _ = SafeDispose(capture); } };
    }

    private async Task RunOperation(Func<long, Task> action)
    {
        if (_operations.TryBegin() is not { } generation) return;
        _busy = true; _review.IsEnabled = false; _save.IsEnabled = false; _confirm.IsEnabled = false;
        _ack.IsEnabled = false; _phrase.IsEnabled = false;
        try { await action(generation); }
        catch { if (!_closed && _operations.Current(generation)) { _plan = null; SafeStop(_capture);
            _message.Text = "Operation unavailable. Check the fresh review, exact phrase, loaded tab or new local summary filename. Capture remains stopped."; } }
        finally { _operations.End(); _busy = false; if (!_closed) { _review.IsEnabled = true; _save.IsEnabled = true;
            _ack.IsEnabled = true; _phrase.IsEnabled = true; Refresh(); } }
    }
    private static void SafeStop(WindowsConsentedMetadataSession? capture) { try { capture?.Stop(); } catch { } }
    private static async Task SafeDispose(WindowsConsentedMetadataSession capture)
    { SafeStop(capture); try { await capture.DisposeAsync(); } catch { } }
    private static object BuildSummary(WindowsConsentedMetadataSession capture)
    {
        var status = capture.Status(); var page = capture.Read(limit: 1000);
        var location = typeof(WindowsConsentedMetadataSession).Assembly.Location;
        string hash = "UNAVAILABLE";
        if (!string.IsNullOrEmpty(location)) { using var assembly = File.OpenRead(location); hash = Convert.ToHexString(SHA256.HashData(assembly)); }
        return NativeMetadataSummary.Create(status, page, hash);
    }

    private void Refresh()
    {
        if (_closed) return;
        try
        {
            _confirm.IsEnabled = !_busy && _capture is not null && _plan is not null && _capture.IsPending(_plan) &&
                _ack.IsChecked == true && _phrase.Text == "ALLOW METADATA";
            _status.Text = _capture is null ? "Capture OFF" : JsonSerializer.Serialize(_capture.Status(), TraceNormalizer.JsonOptions);
        }
        catch { _confirm.IsEnabled = false; _status.Text = "Capture unavailable"; }
    }
    private static T Named<T>(T control, string id, string name) where T : Control
    { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); return control; }
}
