using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Runtime.Versioning;
using System.Text.Json;
using SupraChat.Core;

namespace SupraChat.PeerLedgerRdte;

[SupportedOSPlatform("windows")]
internal sealed class MetadataConsentPane : UserControl
{
    private readonly SharedDesktopHost _host;
    private readonly Func<int> _selected;
    private MetadataConsentPlan? _pending;
    private int _pendingSession;
    private readonly CheckBox _ack = Named(new CheckBox { Content = "I reviewed this session and metadata scope", IsEnabled = false }, "Consent.Acknowledge", "Acknowledge this session's metadata review");
    private readonly Button _confirm = Named(new Button { Content = "Start reviewed synthetic capture", IsEnabled = false }, "Consent.Confirm", "Confirm reviewed metadata capture");
    private readonly TextBox _review = Named(new TextBox { IsReadOnly = true, Height = 115, AcceptsReturn = true, Text = "Review required" }, "Consent.Review", "Metadata consent review");
    private readonly TextBox _status = Named(new TextBox { IsReadOnly = true, Height = 95, AcceptsReturn = true }, "Consent.Status", "Metadata capture consent status");
    private readonly TextBox _action = Named(new TextBox { IsReadOnly = true, Text = "Capture off" }, "Consent.Action", "Metadata capture action result");
    private readonly TextBlock _summary = Named(new TextBlock { Text = "Choose a session, then review its capture scope.", TextWrapping = TextWrapping.Wrap }, "Consent.Summary", "Session capture scope summary");

    public MetadataConsentPane(SharedDesktopHost host, Func<int> selected)
    {
        _host = host; _selected = selected;
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Review metadata capture", FontSize = 22 });
        panel.Children.Add(new TextBlock
        {
            Text = "Prototype: synthetic events only. Review expires after one minute; capture lasts two minutes for this session and view. " +
                "Up to 256 metadata events stay in memory. Stop prevents new events and retains earlier events. " +
                "No prompts, responses, cookies, headers, bodies or automatic uploads. This test does not approve live ChatGPT capture.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(Button("Review synthetic metadata", "Consent.Preview", () => Preview(false)));
        panel.Children.Add(Button("Check first-party availability", "Consent.FirstParty", () => Preview(true)));
        panel.Children.Add(_summary); panel.Children.Add(_review); panel.Children.Add(_ack); panel.Children.Add(_confirm);
        panel.Children.Add(Button("Stop capture", "Consent.Stop", () => { _host.ConsentStop(_selected()); ClearReview(); _action.Text = "Capture stopped"; }));
        panel.Children.Add(Button("Emit synthetic event", "Consent.Emit", () => _action.Text = _host.ConsentEmit(_selected()) ? "Event recorded" : "Event refused"));
        panel.Children.Add(Button("Replace test view", "Consent.ReplaceView", () => { _host.ConsentReplaceView(_selected()); ClearReview(); _action.Text = "View replaced; review required"; }));
        panel.Children.Add(Button("Expire all test leases", "Consent.Expire", () => { _host.ConsentExpire(); ClearReview(); _action.Text = "Lease expired"; }));
        panel.Children.Add(_action); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel };
        _ack.PropertyChanged += (_, args) => { if (args.Property == CheckBox.IsCheckedProperty) UpdateConfirmation(); };
        _confirm.Click += (_, _) => Run(() =>
        {
            if (_pending is null) return;
            var result = _host.ConsentConfirm(_selected(), _pending, _ack.IsChecked == true);
            _pending = null; _ack.IsChecked = false; _ack.IsEnabled = false; UpdateConfirmation();
            _action.Text = result.Outcome == "ALLOWED" ? "Synthetic metadata capture active" : "Review refused";
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { Refresh(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }
    private Button Button(string text, string id, Action action)
    {
        var button = Named(new Button { Content = text }, id, text);
        button.Click += (_, _) => Run(action); return button;
    }
    private void Run(Action action)
    { try { action(); } catch { _action.Text = "Consent operation unavailable"; } finally { Refresh(); } }
    private void Preview(bool firstParty)
    {
        ClearReview();
        _pendingSession = _selected(); _pending = _host.ConsentPreview(_pendingSession, firstParty);
        _review.Text = JsonSerializer.Serialize(_pending.Review, TraceNormalizer.JsonOptions);
        _summary.Text = $"Session {(_pendingSession == 0 ? "A" : "B")}, view {_pending.Review.ViewRevision}. " +
            (_pending.Review.Available ? "Synthetic metadata only, for up to two minutes after confirmation. Earlier events remain when stopped." :
                "First-party capture is unavailable. This review cannot enable live ChatGPT capture.");
        _ack.IsEnabled = _pending.Review.Available;
        _action.Text = _pending.Review.Available ? "Review this session before confirming" : "First-party capture unavailable";
        UpdateConfirmation();
    }
    private void ClearReview()
    {
        if (_pending is not null) _host.ConsentCancel(_pendingSession, _pending);
        _pending = null; _ack.IsChecked = false; _ack.IsEnabled = false; _confirm.IsEnabled = false;
        _review.Text = "Review required";
        _summary.Text = "Review this session and current view before enabling capture.";
    }
    public void SelectionChanged() => Run(() => { ClearReview(); _action.Text = "Session changed; review required"; });
    private void UpdateConfirmation()
    {
        var pending = false;
        try { pending = _pending?.Review.Available == true && _host.ConsentReviewPending(_selected(), _pending); } catch { }
        _ack.IsEnabled = pending;
        _confirm.IsEnabled = pending && _ack.IsChecked == true;
        if (_pending?.Review.Available == true && !pending) _action.Text = "Review expired or invalidated; review again";
    }
    private void Refresh()
    {
        try { _status.Text = JsonSerializer.Serialize(_host.ConsentStatus(_selected()), TraceNormalizer.JsonOptions); UpdateConfirmation(); }
        catch { _status.Text = "{\"state\":\"UNAVAILABLE\",\"capture_active\":false}"; _ack.IsEnabled = false; _confirm.IsEnabled = false; }
    }
    private static T Named<T>(T control, string id, string name) where T : Control
    { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); return control; }
}
