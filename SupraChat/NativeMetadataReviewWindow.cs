using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SupraChat.Core;

namespace SupraChat;

/// <summary>Local interactive experiment only. Never constructed by public synthetic qualification.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeMetadataReviewWindow : Window
{
    internal sealed record CloseResult(bool ClosedObserved, bool AggregateSaved, string Failure, NativeMetadataStatus? Status);
    private readonly TaskCompletionSource<CloseResult> _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<CloseResult> CloseCompletion => _closeCompletion.Task;
    private TaskCompletionSource<bool>? _operationCompletion;
    private bool _allowClose;
    private WindowsConsentedMetadataSession? _capture;
    private WindowsConsentedMetadataSession? _savedCapture;
    private WindowsConsentedMetadataSession? _retiringCapture;
    private (WindowsConsentedMetadataSession Capture, NativeMetadataStatus Status, JsonElement Summary)? _retiringSnapshot;
    private MetadataConsentPlan? _plan;
    private readonly ReviewOperationFence _operations = new();
    private readonly NativeMetadataLifecycleReadback _lifecycle = new();
    private readonly TextBox _lifecycleView = Named(new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 130 },
        "Native.Metadata.LifecycleReadback", "Sanitized native metadata lifecycle history");
    private bool _closed, _busy;
    private readonly CheckBox _ack = Named(new CheckBox { Content = "I reviewed this window and metadata-only scope" }, "Native.Metadata.Acknowledge", "Acknowledge the local metadata experiment");
    private readonly Button _review = Named(new Button { Content = "Prepare this window's review" }, "Native.Metadata.Prepare", "Prepare metadata review");
    private readonly Button _confirm = Named(new Button { Content = "Confirm two-minute metadata capture", IsEnabled = false }, "Native.Metadata.Confirm", "Confirm reviewed first-party metadata capture");
    private readonly TextBlock _message = Named(new TextBlock { Text = "Capture OFF. No network observer is attached.", TextWrapping = TextWrapping.Wrap }, "Native.Metadata.Message", "Native metadata experiment status");
    private readonly TextBox _status = Named(new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 160 }, "Native.Metadata.Status", "Sanitized metadata capture state");
    private readonly Button _save = Named(new Button { Content = "Save summary" }, "Native.Metadata.Save", "Save aggregate metadata experiment summary");
    private readonly TextBlock _readiness = Named(new TextBlock { TextWrapping = TextWrapping.Wrap }, "Native.Metadata.Readiness", "Capture confirmation readiness");

    internal NativeMetadataReviewWindow(Guid sessionId, Func<IntPtr> currentHandle, string outputDirectory)
        : this(acknowledged =>
        {
            var handle = currentHandle();
            var optIn = LocalFirstPartyMetadataOptIn.FromExplicitReview(sessionId, handle, acknowledged);
            return WindowsConsentedMetadataSession.PrepareFirstParty(handle, sessionId, currentHandle, optIn);
        }, outputDirectory, false) { }

    internal static NativeMetadataReviewWindow CreateSyntheticQualification(Guid sessionId, Func<IntPtr> currentHandle,
        Uri loopbackOrigin, string outputDirectory, TimeProvider clock) => new(acknowledged =>
        {
            if (!acknowledged) throw new NativeMetadataPreparationException(NativeMetadataPreparationReason.AcknowledgementRequired);
            return WindowsConsentedMetadataSession.PrepareSynthetic(currentHandle(), sessionId, loopbackOrigin, currentHandle, clock);
        }, outputDirectory, true);

    private NativeMetadataReviewWindow(Func<bool, WindowsConsentedMetadataSession> prepareCapture, string outputDirectory, bool synthetic)
    {
        Title = synthetic ? "SupraChat synthetic metadata review qualification" : "Review ChatGPT metadata experiment"; Width = 690; Height = 650;
        AutomationProperties.SetAutomationId(this, "Native.Metadata.Window");
        var summaryStore = new NativeMetadataSummaryStore(outputDirectory, synthetic);
        _lifecycle.Record(NativeMetadataLifecycleReadback.Action.Opened, null);
        _lifecycleView.Text = _lifecycle.ToUiValue();
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Local metadata-only experiment", FontSize = 24 });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            (synthetic ? "Scope: disposable synthetic loopback fixture only. " : "Scope: this browser window at chatgpt.com, for at most two minutes. ") +
            "Only origin category, event timing, direction, " +
            "status, byte counts and pseudonymous correlation are retained in memory (up to 256 events). " +
            "CDP event JSON, including off-origin requests, reaches this process before origin filtering; headers, cookies, prompts, responses and frame payloads are not retained or exported. " +
            "Stop, navigation, view replacement or closing this window ends capture. Closing this review saves an aggregate summary automatically in the local experiment folder after cleanup; no raw events are saved. " +
            "No provider action, automatic upload, conversation binding or verified-human claim is enabled. " +
            "Use only after source review and your explicit opt-in for this test. Keep the ChatGPT tab open." });
        foreach (var control in new Control[] { _ack, _review, _confirm }) panel.Children.Add(control);
        var stop = Named(new Button { Content = "Stop capture" }, "Native.Metadata.Stop", "Stop first-party metadata capture");
        panel.Children.Add(stop); panel.Children.Add(_save); panel.Children.Add(_readiness); panel.Children.Add(_message); panel.Children.Add(_status); panel.Children.Add(_lifecycleView);
        Content = new ScrollViewer { Content = panel };
        _review.Click += async (_, _) => await RunOperation(async generation =>
        {
            _confirm.IsEnabled = false;
            var prior = _capture; _capture = null; _plan = null;
            _retiringCapture = prior;
            if (prior is not null)
            {
                try
                {
                    SafeStop(prior);
                    await prior.StopForSummaryAsync();
                    _retiringSnapshot = (prior, prior.Status(), (JsonElement)BuildSummary(prior, synthetic));
                }
                finally { await SafeDispose(prior); }
            }
            if (!_operations.Current(generation)) return;
            _retiringCapture = null; _retiringSnapshot = null;
            var prepared = prepareCapture(_ack.IsChecked == true);
            if (!_operations.Current(generation)) { await SafeDispose(prepared); return; }
            _capture = prepared; _plan = prepared.Preview();
            _lifecycle.Record(NativeMetadataLifecycleReadback.Action.Prepared, prepared.Status());
            _message.Text = "Review ready for this local browser session. Confirm within one minute. Capture remains OFF.";
        });
        _confirm.Click += async (_, _) => await RunOperation(async generation =>
        {
            _confirm.IsEnabled = false;
            if (_capture is not { } capture || _plan is not { } plan) return;
            _plan = null;
            var reviewed = _ack.IsChecked == true;
            _ack.IsChecked = false;
            var attached = await capture.ConfirmAsync(plan, reviewed);
            if (!_operations.Current(generation)) return;
            _lifecycle.Record(attached ? NativeMetadataLifecycleReadback.Action.Confirmed : NativeMetadataLifecycleReadback.Action.Refused, capture.Status());
            _message.Text = attached ? "Metadata capture active. Normal browser activity remains authoritative; no provider action was requested." :
                "Observer did not start or consent expired. Prepare a new review to try again.";
        });
        stop.Click += (_, _) =>
        {
            _operations.Cancel(); _plan = null; SafeStop(_capture); _ack.IsChecked = false;
            _lifecycle.Record(NativeMetadataLifecycleReadback.Action.StopRequested, _capture?.Status());
            _message.Text = "Capture stopped. Earlier metadata remains until this review window closes."; Refresh();
        };
        _save.Click += async (_, _) => await RunOperation(async generation =>
        {
                if (_capture is not { } capture) { _message.Text = "No experiment to summarize."; return; }
                _lifecycle.Record(NativeMetadataLifecycleReadback.Action.SaveRequested, capture.Status());
                _plan = null; await capture.StopForSummaryAsync();
                if (!_operations.Current(generation)) return;
                _lifecycle.Record(NativeMetadataLifecycleReadback.Action.Observed, capture.Status());
                var summary = BuildSummary(capture, synthetic);
                summaryStore.Save(NativeMetadataSummary.AttachLifecycle((JsonElement)summary, _lifecycle));
                _savedCapture = capture;
                _lifecycle.Record(NativeMetadataLifecycleReadback.Action.SummarySaved, capture.Status());
                _message.Text = "Stopped and saved an aggregate summary. No event IDs, URLs, titles or payloads were exported.";
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => timer.Start();
        JsonElement? closeSummary = null;
        NativeMetadataStatus? closeStatus = null;
        var closeFailure = "NONE";
        var alreadySaved = false;
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (_closed) return; // Duplicate requests share this finalization.
            _closed = true; _operations.Close(); timer.Stop(); _plan = null;
            _lifecycle.Record(NativeMetadataLifecycleReadback.Action.CloseRequested, null);
            _review.IsEnabled = _confirm.IsEnabled = _save.IsEnabled = _ack.IsEnabled = stop.IsEnabled = false;
            var capture = _capture ?? _retiringCapture;
            var pending = _operationCompletion?.Task;
            try { capture?.StopForReviewClose(); }
            catch { if (_retiringSnapshot is not { } snapshot || !ReferenceEquals(snapshot.Capture, capture)) closeFailure = "STOP_FAILED"; }
            _ = FinalizeClose();

            async Task FinalizeClose()
            {
                try
                {
                    // Preparation or Save may already own the capture. Fence it, then await it.
                    if (pending is not null)
                    {
                        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException) { closeFailure = "OPERATION_CLEANUP_PENDING"; }
                    }
                    if (capture is not null)
                    {
                        alreadySaved = ReferenceEquals(_savedCapture, capture);
                        if (_retiringSnapshot is { } retired && ReferenceEquals(retired.Capture, capture))
                        {
                            closeStatus = retired.Status;
                            if (!alreadySaved) closeSummary = retired.Summary;
                        }
                        else
                        {
                            await capture.StopForSummaryAsync();
                            closeStatus = capture.Status();
                            if (!alreadySaved) closeSummary = (JsonElement)BuildSummary(capture, synthetic);
                        }
                    }
                }
                catch { closeFailure = "SUMMARY_PREPARATION_FAILED"; }
                finally
                {
                    if (capture is not null) await SafeDispose(capture);
                    _capture = null; _retiringCapture = null; _retiringSnapshot = null;
                    _allowClose = true; Dispatcher.UIThread.Post(Close);
                }
            }
        };
        Closed += (_, _) =>
        {
            // Persist only after actual closure, never merely because Closing was requested.
            var saved = alreadySaved;
            // The actual Closed event precedes creation of the aggregate. The write
            // itself proves persistence; a failed write cannot claim saved evidence.
            _lifecycle.Record(NativeMetadataLifecycleReadback.Action.Closed, closeStatus);
            if (closeSummary is { } summary)
            {
                try { summaryStore.Save(NativeMetadataSummary.AttachLifecycle(summary, _lifecycle)); saved = true; }
                catch { closeFailure = "SUMMARY_SAVE_FAILED"; _lifecycle.Record(NativeMetadataLifecycleReadback.Action.SaveFailed, closeStatus); }
            }
            _closeCompletion.TrySetResult(new(true, saved, closeFailure, closeStatus));
        };
    }

    private async Task RunOperation(Func<long, Task> action)
    {
        if (_operations.TryBegin() is not { } generation) return;
        var completion = _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _busy = true; _review.IsEnabled = false; _save.IsEnabled = false; _confirm.IsEnabled = false;
        _ack.IsEnabled = false;
        try { await action(generation); }
        catch (Exception error) { if (!_closed && _operations.Current(generation)) { _plan = null; SafeStop(_capture);
            _lifecycle.Record(NativeMetadataLifecycleReadback.Action.Refused, _capture?.Status());
            _message.Text = error is NativeMetadataPreparationException preparation ? PreparationMessage(preparation.Reason) :
                "The native review or local save could not complete. Capture is stopped. Check the loaded ChatGPT tab and local output folder. [NATIVE_OPERATION_UNAVAILABLE]"; } }
        finally { _operations.End(); completion.TrySetResult(true); _busy = false; if (!_closed) { _review.IsEnabled = true; _save.IsEnabled = true;
            _ack.IsEnabled = true; Refresh(); } }
    }
    private static void SafeStop(WindowsConsentedMetadataSession? capture) { try { capture?.Stop(); } catch { } }
    private static async Task SafeDispose(WindowsConsentedMetadataSession capture)
    { SafeStop(capture); try { await capture.DisposeAsync(); } catch { } }
    private static object BuildSummary(WindowsConsentedMetadataSession capture, bool synthetic)
    {
        var status = capture.Status(); var page = capture.Read(limit: 1000);
        var location = typeof(WindowsConsentedMetadataSession).Assembly.Location;
        string hash = "UNAVAILABLE";
        if (!string.IsNullOrEmpty(location)) { using var assembly = File.OpenRead(location); hash = Convert.ToHexString(SHA256.HashData(assembly)); }
        return synthetic ? NativeMetadataSummary.CreateSynthetic(status, page, hash) : NativeMetadataSummary.Create(status, page, hash);
    }

    private void Refresh()
    {
        if (_closed) return;
        try
        {
            var status = _capture?.Status();
            var pending = !_busy && _capture is not null && _plan is not null && _capture.IsPending(_plan);
            _confirm.IsEnabled = pending && _ack.IsChecked == true;
            _readiness.Text = _busy ? "Finishing the previous operation…" : status?.Consent.CaptureActive == true ? "Capture is active. Stop capture or save the summary when finished." :
                status is not null && status.EndReason != NativeCaptureEndReason.None ? "Capture is off. Save summary writes directly to the local experiment folder." :
                _ack.IsChecked != true ? "Check the scope acknowledgement to prepare or confirm." :
                _capture is null ? "Prepare this window's review first." : _plan is null ? "Prepare a fresh review to start another capture." :
                !pending ? "The review expired or the browser view changed. Prepare a fresh review." : "Ready. Click Confirm two-minute metadata capture within one minute.";
            if (status is not null && status.EndReason != NativeCaptureEndReason.None && _message.Text?.StartsWith("Metadata capture active") == true)
                _message.Text = status.EndReason == NativeCaptureEndReason.LeaseExpired ? "Capture expired after two minutes. Save the summary when ready." : "Capture is off. Save the summary when ready.";
            _status.Text = status is null ? "Capture OFF" : JsonSerializer.Serialize(status, TraceNormalizer.JsonOptions);
            _lifecycle.Record(NativeMetadataLifecycleReadback.Action.Observed, status);
            _lifecycleView.Text = _lifecycle.ToUiValue();
        }
        catch { _confirm.IsEnabled = false; _status.Text = "Capture unavailable";
            _lifecycleView.Text = _lifecycle.ToUiValue();
            _readiness.Text = "The browser view is unavailable. Keep the ChatGPT tab selected and wait for it to load."; }
    }
    private static string PreparationMessage(NativeMetadataPreparationReason reason) => reason switch
    {
        NativeMetadataPreparationReason.AcknowledgementRequired => "Check the scope acknowledgement, then prepare the review. [ACKNOWLEDGEMENT_REQUIRED]",
        NativeMetadataPreparationReason.NativeViewUnavailable => "The native browser view is unavailable. Select the ChatGPT tab in the main window and wait for it to load. [NATIVE_VIEW_UNAVAILABLE]",
        NativeMetadataPreparationReason.NativeViewChanged => "The native browser view changed during preparation. Prepare a fresh review with the ChatGPT tab kept open. [NATIVE_VIEW_CHANGED]",
        NativeMetadataPreparationReason.OriginNotReady => "The loaded browser is not ready at the admitted origin. Finish loading ChatGPT in its tab, then prepare again. [ORIGIN_NOT_READY]",
        NativeMetadataPreparationReason.EnvironmentOverride => "A WebView2 environment override prevents this experiment. Capture remains off. [ENVIRONMENT_OVERRIDE]",
        NativeMetadataPreparationReason.ReviewNotReady => "The view changed or is still loading. Stop any active capture and prepare a fresh review. [REVIEW_NOT_READY]",
        _ => "The native browser interface could not prepare. Capture remains off. [NATIVE_INTEROP_UNAVAILABLE]"
    };
    private static T Named<T>(T control, string id, string name) where T : Control
    { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); return control; }
}
