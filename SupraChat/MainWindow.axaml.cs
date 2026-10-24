using System.Diagnostics;
using System.Text.Json;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SupraChat.Core;

namespace SupraChat;

public partial class MainWindow : Window
{
    private readonly SiwcClient _siwc = new();
    private readonly ResponsesClient _responses = new();
    private readonly SupraChatCore _core = new();
    private readonly List<ResponseAttachment> _attachments = new();
    private readonly Queue<CodexProtocolEvent> _pendingServerRequests = new();
    private readonly SessionWorkbench _workbenchSessions = new();
    private bool _refreshingWorkbenchList;
    private readonly WorkbenchLocalDrafts _workbenchLocalDrafts;
    private readonly WorkbenchTerminalSupervisor _workbenchTerminalSupervisor;
    private long _sharedLedgerObservationEpoch;
    // Host observation is never the local WorkbenchSession authority.
    private SharedSessionInventoryReceipt? _lastSharedHostObservation;
    private int? _sharedFollowHostPort;
    private long? _sharedFollowSequence;
    private string? _sharedFollowSelectedId;
    private bool _updatingSharedFollowSelection;
    private bool _terminalShutdownInProgress;
    private bool _verifiedTerminalShutdown;
    private string? _currentDraftReviewId;
    private WorkbenchMetadataPreview? _pendingMetadataExport;

    private SiwcCredential? _credential;
    private CodexAppServerClient? _codex;
    private CodexAppServerClient.CodexEventSubscription? _codexEvents;
    private CancellationTokenSource? _codexEventsCts;
    private Task? _codexMonitorTask;

    private ResponsesWebSocketClient? _responsesWebSocket;
    private CancellationTokenSource? _responsesWebSocketCts;
    private Task? _responsesWebSocketMonitorTask;
    private WindowsCompanionHotkey? _windowsCompanionHotkey;
    private BrowserSession? _browserSession;
    private AccessibilityPreferences _accessibilityPreferences = AccessibilityPreferences.Default;

    public MainWindow()
    {
        _workbenchLocalDrafts = new WorkbenchLocalDrafts(_workbenchSessions);
        _workbenchTerminalSupervisor = new WorkbenchTerminalSupervisor(_workbenchSessions);
        InitializeComponent();
        RefreshWorkbenchList("No local sessions. Terminal execution and chat submission are disabled.");
        LoadCapabilityCatalog();
        Opened += async (_, _) =>
        {
            var operation = DogfoodObservability.BeginOperation("ui-open");
            await DogfoodObservability.RecordOperationAsync("ui", "window-opened", "start", operation);
            InitializeDesktopIntegration();
            await LoadAccessibilityPreferencesAsync();
            RefreshAccessibilityStatus();
            await RefreshMachineSurfaceAsync();
            await DogfoodObservability.RecordOperationAsync(
                "ui",
                "window-ready",
                "success",
                operation,
                new Dictionary<string, object?>
                {
                    ["platform"] = PlatformLabel(),
                    ["render_scaling"] = RenderScaling
                });
        };
        ScalingChanged += (_, _) => RefreshAccessibilityStatus();
        _ = InitializeAgentLabAsync();
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        var primaryModifier = OperatingSystem.IsMacOS()
            ? KeyModifiers.Meta
            : KeyModifiers.Control;

        if ((e.KeyModifiers & primaryModifier) != primaryModifier)
            return;

        switch (e.Key)
        {
            case Key.D1:
                RootTabs.SelectedIndex = 0;
                ChatView.Focus();
                e.Handled = true;
                break;
            case Key.D2:
                RootTabs.SelectedIndex = 1;
                PromptBox.Focus();
                e.Handled = true;
                break;
            case Key.D3:
                RootTabs.SelectedIndex = 2;
                e.Handled = true;
                break;
            case Key.D4:
                RootTabs.SelectedIndex = 3;
                WorkbenchList.Focus();
                e.Handled = true;
                break;
            case Key.L:
                RootTabs.SelectedIndex = 1;
                PromptBox.Focus();
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                _ = ChangeAccessibilityScaleAsync(AccessibilityPreferencesStore.ScaleStep);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                _ = ChangeAccessibilityScaleAsync(-AccessibilityPreferencesStore.ScaleStep);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                _ = SaveAccessibilityPreferencesAsync(1.0, _accessibilityPreferences.ReducedMotion);
                e.Handled = true;
                break;
        }
    }

    private void RefreshWorkbenchList(string? notice = null)
    {
        var snapshot = _workbenchSessions.Snapshot();
        // Local workbench lifecycle must never change the observed host's
        // separate selection/cursor or grant extra authority to local tabs.
        _pendingMetadataExport = null;
        WorkbenchMetadataExportPreview.Text = string.Empty;
        WorkbenchMetadataExportStatus.Text = "Session metadata changed or selection refreshed; prior export approval cleared.";
        _refreshingWorkbenchList = true;
        try
        {
            WorkbenchList.ItemsSource = snapshot.Sessions;
            WorkbenchList.SelectedItem = snapshot.Sessions
                .FirstOrDefault(item => item.Id == snapshot.ActiveSessionId);
        }
        finally
        {
            _refreshingWorkbenchList = false;
        }
        var conversationCollisions = snapshot.Collisions.Count(c => c.Type == "DUPLICATE_CONVERSATION");
        var workspaceCollisions = snapshot.Collisions.Count(c => c.Type == "SHARED_WORKSPACE");
        if (snapshot.ActiveSessionId is string activeId)
        {
            var drafts = _workbenchLocalDrafts.Read(activeId);
            WorkbenchTerminalDraft.Text = drafts.TerminalInputDraft;
            WorkbenchChatDraft.Text = drafts.ChatInputDraft;
        }
        else
        {
            WorkbenchTerminalDraft.Text = string.Empty;
            WorkbenchChatDraft.Text = string.Empty;
        }
        RefreshWorkbenchTerminalStatus();
        WorkbenchStatus.Text = $"{notice ?? "Session metadata updated."} " +
            $"{snapshot.Sessions.Count}/{snapshot.MaxSessions} sessions; " +
            $"duplicate-conversation warnings={conversationCollisions}; " +
            $"shared-workspace warnings={workspaceCollisions}. " +
            "Browser submission and terminal execution DISABLED.";
    }

    private void ClearWorkbenchTransfer()
    {
        _currentDraftReviewId = null;
        _workbenchLocalDrafts.DiscardPending();
        WorkbenchTransferInput.Text = string.Empty;
        WorkbenchTransferPreview.Text = string.Empty;
        WorkbenchDraftStatus.Text = "Previous transfer review discarded. Local drafts are unchanged.";
    }

    private void WorkbenchCreate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var session = _workbenchSessions.Open(
                WorkbenchTitleInput.Text, WorkbenchUrlInput.Text, WorkbenchWorkspaceInput.Text);
            ClearWorkbenchTransfer();
            RefreshWorkbenchList($"Created {session.Title} as an in-memory descriptor only.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            WorkbenchStatus.Text = $"Create refused: {ex.Message}. No external changes made.";
        }
    }

    private void WorkbenchList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingWorkbenchList || WorkbenchList.SelectedItem is not WorkbenchSession selected)
            return;
        if (_workbenchSessions.Select(selected.Id))
        {
            ClearWorkbenchTransfer();
            RefreshWorkbenchList($"Selected {selected.Title}.");
        }
    }

    private async void WorkbenchClose_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkbenchList.SelectedItem is not WorkbenchSession selected)
        {
            WorkbenchStatus.Text = "Select a session to close.";
            return;
        }
        try
        {
            await _workbenchTerminalSupervisor.ForgetSessionAsync(selected.Id);
        }
        catch (InvalidOperationException)
        {
            WorkbenchTerminalStatus.Text =
                "Cannot close: child process termination was not verified. " +
                "Inspect the OS process and stop it before retrying.";
            return;
        }
        if (!_workbenchSessions.Close(selected.Id))
        {
            WorkbenchStatus.Text = "Selected session no longer exists.";
            return;
        }
        _workbenchLocalDrafts.ForgetSession(selected.Id);
        ClearWorkbenchTransfer();
        RefreshWorkbenchList("Closed metadata session, erased local drafts and stopped any owned child.");
    }

    private void WorkbenchMove(int direction)
    {
        if (WorkbenchList.SelectedItem is not WorkbenchSession selected)
        {
            WorkbenchStatus.Text = "Select a session before reordering.";
            return;
        }
        var index = _workbenchSessions.Snapshot().Sessions.ToList().FindIndex(s => s.Id == selected.Id);
        if (!_workbenchSessions.Move(selected.Id, index + direction))
        {
            WorkbenchStatus.Text = "Session is already at the edge.";
            return;
        }
        ClearWorkbenchTransfer();
        RefreshWorkbenchList("Reordered the local session list; pending transfer reviews invalidated.");
    }

    private void WorkbenchMoveUp_Click(object? sender, RoutedEventArgs e) => WorkbenchMove(-1);
    private void WorkbenchMoveDown_Click(object? sender, RoutedEventArgs e) => WorkbenchMove(1);

    private void WorkbenchPreview(string direction)
    {
        if (WorkbenchList.SelectedItem is not WorkbenchSession selected)
        {
            WorkbenchStatus.Text = "Select a session before previewing a transfer.";
            return;
        }
        try
        {
            var preview = _workbenchLocalDrafts.Prepare(
                selected.Id, direction, WorkbenchTransferInput.Text ?? string.Empty);
            _currentDraftReviewId = preview.ReviewId;
            WorkbenchTransferPreview.Text = preview.PreviewText;
            WorkbenchDraftStatus.Text = $"Review expires after {preview.ExpiresAfterSeconds} seconds. " +
                "A separate click can stage into a local-only draft; no execute or submit authority.";
            WorkbenchStatus.Text = $"PREVIEW ONLY ({preview.PreviewBytes} UTF-8 bytes): " +
                "Nothing has been staged, transmitted, or executed.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _currentDraftReviewId = null;
            WorkbenchTransferPreview.Text = string.Empty;
            WorkbenchStatus.Text = $"Preview refused: {ex.Message}. No external changes made.";
        }
    }

    private void WorkbenchPreviewToTerminal_Click(object? sender, RoutedEventArgs e) =>
        WorkbenchPreview("chat-to-terminal");
    private void WorkbenchPreviewToChat_Click(object? sender, RoutedEventArgs e) =>
        WorkbenchPreview("terminal-to-chat");

    private void WorkbenchStageReviewed_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkbenchList.SelectedItem is not WorkbenchSession selected ||
            _currentDraftReviewId is null)
        {
            WorkbenchDraftStatus.Text = "Review and select a session before staging text locally.";
            return;
        }
        try
        {
            var receipt = _workbenchLocalDrafts.StageReviewed(
                selected.Id, _currentDraftReviewId, WorkbenchTransferInput.Text ?? string.Empty);
            _currentDraftReviewId = null;
            WorkbenchTransferPreview.Text = string.Empty;
            WorkbenchTransferInput.Text = string.Empty;
            var drafts = _workbenchLocalDrafts.Read(selected.Id);
            WorkbenchTerminalDraft.Text = drafts.TerminalInputDraft;
            WorkbenchChatDraft.Text = drafts.ChatInputDraft;
            WorkbenchDraftStatus.Text = $"Staged reviewed text into {receipt.Destination}. " +
                "No ChatGPT submission, terminal execution or network request occurred.";
        }
        catch (InvalidOperationException)
        {
            _currentDraftReviewId = null;
            _workbenchLocalDrafts.DiscardPending();
            WorkbenchTransferPreview.Text = string.Empty;
            WorkbenchDraftStatus.Text = "Approval was changed, stale, expired, or targeted a different session. Nothing staged.";
        }
    }

    private void RefreshWorkbenchTerminalStatus()
    {
        if (WorkbenchList?.SelectedItem is not WorkbenchSession selected)
        {
            WorkbenchTerminalStatus.Text = "No selected session child. Terminal input, PTY and chat submission unavailable.";
            return;
        }
        var state = _workbenchTerminalSupervisor.Inspect(selected.Id);
        WorkbenchTerminalStatus.Text = state is null
            ? "No supervised PowerShell child for selected session."
            : $"PowerShell child PID={state.ProcessId}; {state.State}; running={state.Running}; " +
              $"discarded output={state.DiscardedOutputBytes}/{state.MaxOutputBytes} bytes; " +
              $"max observed RSS={state.MaxObservedRssBytes} bytes; TTL={state.MaxLifetimeSeconds}s. " +
              "Output is not retained; no command input or OS sandbox.";
    }

    private void WorkbenchTerminalStart_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkbenchList.SelectedItem is not WorkbenchSession selected)
        {
            WorkbenchTerminalStatus.Text = "Select a local workbench session before starting a child.";
            return;
        }
        try
        {
            var state = _workbenchTerminalSupervisor.Start(
                selected.Id, WorkbenchShellPath.Text ?? string.Empty);
            WorkbenchTerminalStatus.Text =
                $"Started supervised idle PowerShell PID={state.ProcessId}. " +
                "No commands sent, no PTY and no kernel-enforced sandbox. " +
                "Stop manually or auto-stop by observed resource/lifetime limits.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            WorkbenchTerminalStatus.Text =
                $"Child start refused: {ex.GetType().Name}. No command or chat send was attempted.";
        }
    }

    private async void WorkbenchTerminalStop_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkbenchList.SelectedItem is not WorkbenchSession selected)
        {
            WorkbenchTerminalStatus.Text = "Select a session to stop its own child.";
            return;
        }
        try
        {
            await _workbenchTerminalSupervisor.StopAsync(selected.Id);
            RefreshWorkbenchTerminalStatus();
        }
        catch (Exception)
        {
            WorkbenchTerminalStatus.Text = "Child termination is uncertain. Inspect process state before retrying.";
        }
    }

    private void WorkbenchTerminalRefresh_Click(object? sender, RoutedEventArgs e) =>
        RefreshWorkbenchTerminalStatus();

    private void WorkbenchHostSessionList_SelectionChanged(object? sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingSharedFollowSelection || _lastSharedHostObservation is null)
            return;
        if (WorkbenchHostSessionList.SelectedItem is not SharedSessionInventoryRow item)
        {
            _sharedFollowSelectedId = null;
            WorkbenchHostEvents.Text = string.Empty;
            WorkbenchHostFollowStatus.Text = "No observed host session selected. No local tab or browser binding changed.";
            return;
        }
        if (!_lastSharedHostObservation.Sessions.Any(s => s.Id == item.Id))
            return;
        _sharedFollowSelectedId = item.Id;
        // An explicit selection shows only the retained observation window;
        // it does not claim the whole earlier history or change workbench.
        var selected = SharedSessionFeedProjection.Project(
            _lastSharedHostObservation,item.Id,previousSequence:null);
        WorkbenchHostEvents.Text = selected.Events.Count == 0
            ? "No retained metadata events for this host session."
            : string.Join(Environment.NewLine,selected.Events.Select(x => x.DisplayLabel));
        WorkbenchHostFollowStatus.Text =
            $"Host session {item.Id[..8]}, revision {item.Version}. " +
            $"Last observed sequence {selected.Sequence}. {selected.Status}. " +
            "Read-only; no local tab, browser or terminal binding.";
    }

    private async void WorkbenchReadSharedLedger_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WorkbenchLedgerPort.Text, out var port) ||
            port is < 1024 or > 65535)
        {
            WorkbenchLedgerInventory.Text = string.Empty;
            WorkbenchHostEvents.Text = string.Empty;
            WorkbenchLedgerStatus.Text = "Explicit nonprivileged 127.0.0.1 port required; no request sent.";
            WorkbenchHostFollowStatus.Text = "Host observation unavailable; no session binding.";
            return;
        }
        if (_sharedFollowHostPort != port)
        {
            // A port is NOT a durable host identity. A port switch always
            // requires explicit re-selection; no auto-follow across hosts.
            _sharedFollowHostPort = port;
            _sharedFollowSequence = null;
            _sharedFollowSelectedId = null;
            _lastSharedHostObservation = null;
        }
        var epoch = Interlocked.Increment(ref _sharedLedgerObservationEpoch);
        WorkbenchLedgerStatus.Text = "Reading existing shared host metadata only. No session change or ChatGPT submission.";
        try
        {
            var snapshot = await SharedSessionInventoryReader.ObserveAsync(
                port, Environment.GetEnvironmentVariable("SUPRACHAT_LEDGER_TOKEN"));
            if (epoch != Interlocked.Read(ref _sharedLedgerObservationEpoch))
                return;
            if (!snapshot.Connected)
            {
                _lastSharedHostObservation = null;
                _updatingSharedFollowSelection = true;
                try
                {
                    WorkbenchHostSessionList.ItemsSource = null;
                    WorkbenchHostSessionList.SelectedItem = null;
                }
                finally { _updatingSharedFollowSelection = false; }
                WorkbenchLedgerInventory.Text = string.Empty;
                WorkbenchHostEvents.Text = string.Empty;
                WorkbenchLedgerStatus.Text = $"Read refused: {snapshot.Status}. No remote write attempted.";
                WorkbenchHostFollowStatus.Text =
                    "Host unavailable; selection not currently verified. Last cursor retained for gap detection.";
                return;
            }
            var projected = SharedSessionFeedProjection.Project(
                snapshot,_sharedFollowSelectedId,_sharedFollowSequence);
            if (projected.Status is "SEQUENCE_REWIND_RESELECT_REQUIRED" or
                "SELECTED_SESSION_MISSING_RESELECT_REQUIRED")
                _sharedFollowSelectedId = null;
            _sharedFollowSequence = snapshot.Sequence;
            _lastSharedHostObservation = snapshot;
            _updatingSharedFollowSelection = true;
            try
            {
                WorkbenchHostSessionList.ItemsSource = snapshot.Sessions;
                WorkbenchHostSessionList.SelectedItem = snapshot.Sessions
                    .FirstOrDefault(row => row.Id == _sharedFollowSelectedId);
            }
            finally { _updatingSharedFollowSelection = false; }
            WorkbenchLedgerInventory.Text = snapshot.Sessions.Count == 0
                ? "No sessions currently reported by the host."
                : string.Join(Environment.NewLine,
                    snapshot.Sessions.Select(row => row.DisplayLabel));
            WorkbenchHostEvents.Text = projected.Events.Count == 0
                ? "No new retained events for the selected host session."
                : string.Join(Environment.NewLine,
                    projected.Events.Select(row => row.DisplayLabel));
            WorkbenchHostFollowStatus.Text =
                $"Host cursor {projected.Sequence}. {projected.Status}. " +
                (projected.EventGapUnverified
                    ? "Missing/truncated metadata event history: cannot claim continuous catch-up. "
                    : "No gap detected in this bounded observation; historical completeness unproven. ") +
                "Selection is read-only and not bound to local tabs or ChatGPT.";
            WorkbenchLedgerStatus.Text =
                $"Observed {snapshot.Sessions.Count} host sessions; sequence {snapshot.Sequence}. " +
                (snapshot.DurableAcrossRestart ? "Host reports experimental persistence. " :
                    "Host does not report durable recovery. ") +
                "Metadata are an unverified loopback self-report, not authenticated participant or product ChatGPT history.";
        }
        catch (Exception)
        {
            if (epoch == Interlocked.Read(ref _sharedLedgerObservationEpoch))
            {
                _lastSharedHostObservation = null;
                WorkbenchLedgerStatus.Text =
                    "Shared host observation failed; no write or ChatGPT submission attempted.";
                WorkbenchHostFollowStatus.Text =
                    "Read failure; event continuity unverified until another successful observation.";
            }
        }
    }

    private void PrepareWorkbenchMetadataExport(string format)
    {
        try
        {
            var preview = WorkbenchMetadataExport.Prepare(_workbenchSessions.Snapshot(), format);
            _pendingMetadataExport = preview;
            WorkbenchMetadataExportPreview.Text = preview.Content;
            WorkbenchMetadataExportStatus.Text = $"{format.ToUpperInvariant()} preview: " +
                $"{preview.ContentBytes} UTF-8 bytes. {preview.DataWarning} " +
                "Nothing has been saved or transmitted.";
        }
        catch (ArgumentException)
        {
            _pendingMetadataExport = null;
            WorkbenchMetadataExportPreview.Text = string.Empty;
            WorkbenchMetadataExportStatus.Text = "Metadata export preview rejected; no file was written.";
        }
    }

    private void WorkbenchExportJsonPreview_Click(object? sender, RoutedEventArgs e) =>
        PrepareWorkbenchMetadataExport("json");

    private void WorkbenchExportMarkdownPreview_Click(object? sender, RoutedEventArgs e) =>
        PrepareWorkbenchMetadataExport("markdown");

    private async void WorkbenchExportSave_Click(object? sender, RoutedEventArgs e)
    {
        var preview = _pendingMetadataExport;
        if (preview is null)
        {
            WorkbenchMetadataExportStatus.Text = "Preview metadata before choosing a save destination.";
            return;
        }

        // Re-render after the user's explicit preview. Changed session state
        // invalidates approval, even if no UI event was raised.
        var current = WorkbenchMetadataExport.Prepare(_workbenchSessions.Snapshot(), preview.Format);
        if (!string.Equals(current.Content, preview.Content, StringComparison.Ordinal))
        {
            _pendingMetadataExport = null;
            WorkbenchMetadataExportPreview.Text = string.Empty;
            WorkbenchMetadataExportStatus.Text = "Session state changed. Generate and review a fresh export before saving.";
            return;
        }

        try
        {
            var top = TopLevel.GetTopLevel(this)
                ?? throw new InvalidOperationException("Storage provider unavailable.");
            var extension = preview.Format == "json" ? ".json" : ".md";
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save reviewed SupraChat session metadata (contains workspace/conversation references)",
                SuggestedFileName = "suprachat-session-metadata" + extension
            });
            if (file is null)
            {
                WorkbenchMetadataExportStatus.Text = "Export cancelled. No file was saved by SupraChat.";
                return;
            }

            if (_pendingMetadataExport is null ||
                !string.Equals(
                    WorkbenchMetadataExport.Prepare(_workbenchSessions.Snapshot(), preview.Format).Content,
                    preview.Content, StringComparison.Ordinal))
            {
                WorkbenchMetadataExportStatus.Text = "Export approval invalidated during file selection; nothing written.";
                return;
            }

            // No implicit path, silent autosave, browser upload or plaintext
            // transcript read. StorageProvider owns the selected destination.
            await using var output = await file.OpenWriteAsync();
            output.SetLength(0);
            await using (var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true))
            {
                await writer.WriteAsync(preview.Content.AsMemory());
                await writer.FlushAsync();
            }
            _pendingMetadataExport = null;
            WorkbenchMetadataExportStatus.Text =
                $"Reviewed {preview.Format.ToUpperInvariant()} session metadata saved to the selected file. " +
                "No transcripts, draft transfers, terminal output or credentials were exported.";
        }
        catch (Exception)
        {
            WorkbenchMetadataExportStatus.Text =
                "Export failed. The selected destination might contain partial data; inspect it before retrying.";
        }
    }

    private async Task LoadAccessibilityPreferencesAsync()
    {
        _accessibilityPreferences = await AccessibilityPreferencesStore.LoadAsync();
        ApplyAccessibilityPreferences();
    }

    private async Task SaveAccessibilityPreferencesAsync(double scale, bool reducedMotion)
    {
        _accessibilityPreferences = await AccessibilityPreferencesStore.SaveAsync(scale, reducedMotion);
        ApplyAccessibilityPreferences();
    }

    private void ApplyAccessibilityPreferences()
    {
        FontSize = 14 * _accessibilityPreferences.InterfaceScale;
        ReducedMotionBox.IsChecked = _accessibilityPreferences.ReducedMotion;
        AccessibilityPreferencesStatus.Text =
            $"Interface scale {_accessibilityPreferences.InterfaceScale * 100:0}% · " +
            $"reduced motion {(_accessibilityPreferences.ReducedMotion ? "on" : "off")} · " +
            $"keyboard {AccessibilityContract.PrimaryModifierName}++/-/0";
        RefreshAccessibilityStatus();
    }

    private async Task ChangeAccessibilityScaleAsync(double delta) =>
        await SaveAccessibilityPreferencesAsync(
            _accessibilityPreferences.InterfaceScale + delta,
            _accessibilityPreferences.ReducedMotion);

    private async void AccessibilityScaleDown_Click(object? sender, RoutedEventArgs e) =>
        await ChangeAccessibilityScaleAsync(-AccessibilityPreferencesStore.ScaleStep);

    private async void AccessibilityScaleReset_Click(object? sender, RoutedEventArgs e) =>
        await SaveAccessibilityPreferencesAsync(1.0, _accessibilityPreferences.ReducedMotion);

    private async void AccessibilityScaleUp_Click(object? sender, RoutedEventArgs e) =>
        await ChangeAccessibilityScaleAsync(AccessibilityPreferencesStore.ScaleStep);

    private async void ReducedMotionBox_Click(object? sender, RoutedEventArgs e) =>
        await SaveAccessibilityPreferencesAsync(
            _accessibilityPreferences.InterfaceScale,
            ReducedMotionBox.IsChecked == true);

    private void InitializeDesktopIntegration()
    {
        if (!OperatingSystem.IsWindows() || _windowsCompanionHotkey is not null)
            return;

        _windowsCompanionHotkey = new WindowsCompanionHotkey(this);
        var registered = _windowsCompanionHotkey.TryRegister();
        if (!registered)
            AuthStatus.Text = $"Windows companion shortcut {WindowsCompanionHotkey.ShortcutDescription} is unavailable; another application may own it.";
    }

    private async void RefreshMachineSurface_Click(object? sender, RoutedEventArgs e) =>
        await RefreshMachineSurfaceAsync();

    private void RefreshAccessibilityStatus()
    {
        var accessibility = AccessibilityContract.Describe();
        AccessibilityStatus.Text =
            $"screen-reader-semantics={(accessibility.Human.ScreenReaderSemantics ? "ready" : "unavailable")} · " +
            $"keyboard={(accessibility.Human.KeyboardOnly ? "ready" : "unavailable")} · " +
            $"system-theme/high-contrast={(accessibility.Human.HighContrastFollowsPlatform ? "follow-platform" : "custom")} · " +
            $"render-scale={RenderScaling:0.##} · interface-scale={_accessibilityPreferences.InterfaceScale:0.0} · " +
            $"reduced-motion={(_accessibilityPreferences.ReducedMotion ? "on" : "off")} · " +
            $"shortcuts={string.Join(",", accessibility.Human.Shortcuts)}";
    }

    private async Task RefreshMachineSurfaceAsync()
    {
        var credential = await CredentialStore.TryLoadAsync();
        var automationName = OperatingSystem.IsWindows()
            ? "SupraChat.Automation.exe"
            : "SupraChat.Automation";
        var automationPath = Path.Combine(AppContext.BaseDirectory, automationName);
        var codexPath = CodexAppServer.ResolveExecutable();
        var screenCapture = DesktopScreenCapture.Describe();

        MachineExecutablePathBox.Text = automationPath;
        MachineStatus.Text =
            $"platform={PlatformLabel()} · " +
            $"automation={(File.Exists(automationPath) ? "ready" : "not-packaged")} · " +
            $"agent-stdio={(File.Exists(automationPath) ? "ready" : "not-packaged")} · " +
            $"codex={(Path.IsPathRooted(codexPath) && File.Exists(codexPath) ? "bundled" : "PATH-fallback")} · " +
            $"screen-capture={(screenCapture.Implemented ? screenCapture.Adapter : "unavailable")} · " +
            $"authorization={(credential is null ? "required" : credential.HasPlanUsage ? "plan-ready" : "identity-only")}";

        RefreshAudienceParityStatus();
    }

    private void RefreshAudienceParityStatus()
    {
        var parityPath = Path.Combine(
            AppContext.BaseDirectory,
            "oracles",
            "audience-parity-20261001.json");
        AudienceParityPathBox.Text = parityPath;

        if (!File.Exists(parityPath))
        {
            AudienceParityStatus.Text = "Audience parity ledger is not packaged.";
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(parityPath));
            var features = document.RootElement.GetProperty("features");
            var platform = PlatformLabel();
            var total = 0;
            var gaps = 0;
            var qualifying = 0;

            foreach (var feature in features.EnumerateArray())
            {
                total++;
                var hasGap = false;
                var hasQualify = false;

                if (feature.TryGetProperty("access", out var access))
                {
                    foreach (var state in access.EnumerateObject())
                    {
                        hasGap |= string.Equals(state.Value.GetString(), "gap", StringComparison.Ordinal);
                        hasQualify |= string.Equals(state.Value.GetString(), "qualify", StringComparison.Ordinal);
                    }
                }

                if (feature.TryGetProperty("platforms", out var platforms) &&
                    platforms.TryGetProperty(platform, out var platformState))
                {
                    hasGap |= string.Equals(platformState.GetString(), "gap", StringComparison.Ordinal);
                    hasQualify |= string.Equals(platformState.GetString(), "qualify", StringComparison.Ordinal);
                }

                if (hasGap)
                    gaps++;
                else if (hasQualify)
                    qualifying++;
            }

            var explicitWithoutGap = total - gaps - qualifying;
            AudienceParityStatus.Text =
                $"{total} capability families · {explicitWithoutGap} explicit/no-gap · " +
                $"{qualifying} qualifying · {gaps} gaps · platform={platform}. " +
                "Use suprachat-cli parity or parity/read for the complete semantic ledger.";
        }
        catch (Exception ex)
        {
            AudienceParityStatus.Text = $"Audience parity ledger could not be read: {ex.Message}";
        }
    }

    private void OpenStateFolder_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppState.DirectoryPath);

        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", $"\"{AppState.DirectoryPath}\"") { UseShellExecute = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", AppState.DirectoryPath) { UseShellExecute = false }
                : new ProcessStartInfo("xdg-open", AppState.DirectoryPath) { UseShellExecute = false };

        Process.Start(start);
    }

    private async void ExportDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        var operation = DogfoodObservability.BeginOperation("diagnostics-export");
        try
        {
            await DogfoodObservability.RecordOperationAsync(
                "diagnostics",
                "export",
                "start",
                operation);

            var directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                directory = AppState.DirectoryPath;

            var output = Path.Combine(
                directory,
                $"SupraChat-Diagnostics-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.zip");
            var path = await DogfoodObservability.ExportAsync(output);

            await DogfoodObservability.RecordOperationAsync(
                "diagnostics",
                "export",
                "success",
                operation,
                new Dictionary<string, object?>
                {
                    ["file_name"] = Path.GetFileName(path),
                    ["size_bytes"] = new FileInfo(path).Length
                });

            DiagnosticsStatus.Text =
                $"Privacy-safe diagnostics exported: {path}. " +
                $"Trace={DogfoodObservability.Describe().TraceId}; prompts/outputs/tokens excluded.";
        }
        catch (Exception ex)
        {
            await DogfoodObservability.RecordExceptionAsync(
                "diagnostics",
                "export",
                ex,
                operation);
            DiagnosticsStatus.Text = $"Diagnostics export failed: {ex.Message}";
        }
    }

    private static string PlatformLabel() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";

    private async Task InitializeAgentLabAsync()
    {
        var operation = DogfoodObservability.BeginOperation("saved-session-load");
        try
        {
            _credential = await CredentialStore.TryLoadAsync();
            await RefreshRegistrationsAsync(_credential?.ClientId);
            await DogfoodObservability.RecordOperationAsync(
                "auth",
                "saved-session-load",
                "success",
                operation,
                new Dictionary<string, object?>
                {
                    ["credential_present"] = _credential is not null,
                    ["plan_scope_granted"] = _credential?.HasPlanUsage ?? false,
                    ["scope_count"] = _credential?.Scopes.Length ?? 0
                });

            if (_credential is not null)
            {
                AuthStatus.Text = _credential.HasPlanUsage
                    ? $"Saved Agent Lab session loaded for {Short(_credential.Subject)}; ChatGPT-plan scope granted."
                    : $"Saved identity session loaded for {Short(_credential.Subject)}; plan usage is not granted.";
            }
        }
        catch (Exception ex)
        {
            await DogfoodObservability.RecordExceptionAsync("auth", "saved-session-load", ex, operation);
            AuthStatus.Text = $"Saved Agent Lab state could not be loaded: {ex.Message}";
        }
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => ChatView.GoBack();
    private void Forward_Click(object? sender, RoutedEventArgs e) => ChatView.GoForward();
    private void Reload_Click(object? sender, RoutedEventArgs e) => ChatView.Refresh();
    private void Home_Click(object? sender, RoutedEventArgs e) => ChatView.Navigate(new Uri("https://chatgpt.com/"));

    private void ChatView_EnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        var webRoot = Path.Combine(AppState.DirectoryPath, "webview");
        Directory.CreateDirectory(webRoot);

        e.EnableDevTools = false;
        if (e is Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            windows.ProfileName = "SupraChat";
            windows.UserDataFolder = Path.Combine(webRoot, "windows");
            windows.IsInPrivateModeEnabled = false;
        }
        else if (e is Avalonia.Platform.AppleWKWebViewEnvironmentRequestedEventArgs apple)
        {
            apple.NonPersistentDataStore = false;
            apple.DataStoreIdentifier = new Guid("9f2d1f5f-11b7-4e6d-befa-8c69694254af");
            apple.ApplicationNameForUserAgent = "SupraChat";
        }
        else if (e is Avalonia.Platform.GtkWebViewEnvironmentRequestedEventArgs gtk)
        {
            gtk.EphemeralDataManager = false;
            gtk.BaseDataDirectory = Path.Combine(webRoot, "linux", "data");
            gtk.BaseCacheDirectory = Path.Combine(webRoot, "linux", "cache");
        }
    }

    private void OpenExternal_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://chatgpt.com/") { UseShellExecute = true });
    }

    private async void SignIn_Click(object? sender, RoutedEventArgs e) =>
        await CompleteSignInAsync(promptConsent: false);

    private async void UseSavedAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not SiwcRegistration)
        {
            AuthStatus.Text = "Choose a saved ChatGPT account registration first.";
            return;
        }

        await CompleteSignInAsync(promptConsent: false);
    }

    private async void AddAccount_Click(object? sender, RoutedEventArgs e) =>
        await CompleteSignInAsync(promptConsent: false, forceNewRegistration: true);

    private async void ExportCredentialBundle_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this)
                ?? throw new InvalidOperationException("No desktop storage provider is available.");
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export encrypted SupraChat credential bundle",
                SuggestedFileName = $"suprachat-siwc-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json"
            });
            if (file is null)
                return;

            var receipt = await _core.ExportCredentialAsync(
                file.Path.LocalPath,
                CredentialPassphraseBox.Text ?? string.Empty);
            CredentialBundleStatus.Text =
                $"Encrypted credential bundle exported to {receipt.Path}. " +
                $"Generation={receipt.CredentialGeneration}; no token material was written to the receipt.";
        }
        catch (Exception ex)
        {
            CredentialBundleStatus.Text = $"Credential export failed: {ex.Message}";
        }
        finally
        {
            CredentialPassphraseBox.Text = string.Empty;
        }
    }

    private async void ImportCredentialBundle_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this)
                ?? throw new InvalidOperationException("No desktop storage provider is available.");
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import encrypted SupraChat credential bundle",
                AllowMultiple = false
            });
            var file = files.FirstOrDefault();
            if (file is null)
                return;

            var priorToken = _credential?.AccessToken;
            var receipt = await _core.ImportCredentialAsync(
                file.Path.LocalPath,
                CredentialPassphraseBox.Text ?? string.Empty);

            _credential = await CredentialStore.TryLoadAsync();
            await RefreshRegistrationsAsync(_credential?.ClientId);
            if (_credential is not null &&
                !string.Equals(priorToken, _credential.AccessToken, StringComparison.Ordinal))
            {
                await StopCodexAsync();
                await StopResponsesWebSocketAsync();
            }

            CredentialBundleStatus.Text =
                $"Encrypted credential bundle imported. Generation={receipt.CredentialGeneration}; " +
                "this runtime kept its own host identity.";
            AuthStatus.Text = _credential is { HasPlanUsage: true }
                ? "Imported ChatGPT-plan authorization is ready."
                : "Credential bundle imported; ChatGPT-plan usage is not currently granted.";
            if (_credential?.HasPlanUsage == true)
                await PopulateModelsAsync();
        }
        catch (Exception ex)
        {
            CredentialBundleStatus.Text = $"Credential import failed: {ex.Message}";
        }
        finally
        {
            CredentialPassphraseBox.Text = string.Empty;
        }
    }

    private async void EnablePlanUsage_Click(object? sender, RoutedEventArgs e) =>
        await CompleteSignInAsync(promptConsent: true);

    private async Task CompleteSignInAsync(bool promptConsent, bool forceNewRegistration = false)
    {
        var operation = DogfoodObservability.BeginOperation("interactive-sign-in");
        await DogfoodObservability.RecordOperationAsync(
            "auth",
            "interactive-sign-in",
            "start",
            operation,
            new Dictionary<string, object?>
            {
                ["prompt_consent"] = promptConsent,
                ["force_new_registration"] = forceNewRegistration
            });

        try
        {
            AuthStatus.Text = promptConsent
                ? "Opening system browser to request ChatGPT-plan permission…"
                : "Opening system browser for ChatGPT authorization…";

            var hostId = await HostIdentity.LoadOrCreateAsync();
            var active = _credential ?? await CredentialStore.TryLoadAsync();
            var selected = AccountBox.SelectedItem as SiwcRegistration;

            SiwcCredential? existing = active;
            SiwcRegistration? registration = null;

            if (forceNewRegistration)
            {
                existing = null;
            }
            else if (selected is not null &&
                     !string.Equals(active?.ClientId, selected.ClientId, StringComparison.Ordinal))
            {
                existing = null;
                registration = selected;
            }

            var priorToken = _credential?.AccessToken;
            var priorClientId = _credential?.ClientId;

            _credential = await _siwc.SignInAsync(
                this,
                hostId,
                "SupraChat",
                existing,
                promptConsent,
                registration,
                operation.CorrelationId);
            _credential = await _core.CommitInteractiveCredentialAsync(_credential);
            await RefreshRegistrationsAsync(_credential.ClientId);

            if (!string.Equals(priorToken, _credential.AccessToken, StringComparison.Ordinal) ||
                !string.Equals(priorClientId, _credential.ClientId, StringComparison.Ordinal))
            {
                await StopCodexAsync();
                await StopResponsesWebSocketAsync();
            }

            AuthStatus.Text = _credential.HasPlanUsage
                ? $"Agent Lab authorized. Account subject: {Short(_credential.Subject)}; ChatGPT-plan scope granted."
                : $"Identity sign-in completed for {Short(_credential.Subject)}, but ChatGPT-plan usage was not granted.";

            if (_credential.HasPlanUsage)
                await PopulateModelsAsync(operation.CorrelationId);

            await DogfoodObservability.RecordOperationAsync(
                "auth",
                "interactive-sign-in",
                "success",
                operation,
                new Dictionary<string, object?>
                {
                    ["plan_scope_granted"] = _credential.HasPlanUsage,
                    ["scope_count"] = _credential.Scopes.Length
                });
        }
        catch (Exception ex)
        {
            await DogfoodObservability.RecordExceptionAsync("auth", "interactive-sign-in", ex, operation);
            AuthStatus.Text = $"Sign-in did not complete: {ex.Message}";
        }
    }

    private async void SignOut_Click(object? sender, RoutedEventArgs e)
    {
        var operation = DogfoodObservability.BeginOperation("sign-out-revoke");
        await DogfoodObservability.RecordOperationAsync("auth", "sign-out-revoke", "start", operation);
        try
        {
            _credential ??= await CredentialStore.TryLoadAsync();
            if (_credential is null)
            {
                AuthStatus.Text = "No Agent Lab session is stored.";
                return;
            }

            await StopCodexAsync();
            await StopResponsesWebSocketAsync();
            var confirmed = await _siwc.RevokeAsync(_credential, operation.CorrelationId);
            CredentialStore.Clear();
            _credential = null;
            ModelBox.ItemsSource = null;
            await RefreshRegistrationsAsync();
            AuthStatus.Text = confirmed
                ? "Agent Lab renewable session revoked and local tokens cleared; the account registration remains available for reauthorization."
                : "Remote revocation could not be confirmed after a temporary server failure; local tokens were cleared and the account registration was retained.";
            await DogfoodObservability.RecordOperationAsync(
                "auth",
                "sign-out-revoke",
                confirmed ? "success" : "remote-unconfirmed",
                operation,
                new Dictionary<string, object?> { ["local_tokens_cleared"] = true });
        }
        catch (Exception ex)
        {
            await StopCodexAsync();
            await StopResponsesWebSocketAsync();
            CredentialStore.Clear();
            _credential = null;
            ModelBox.ItemsSource = null;
            await RefreshRegistrationsAsync();
            await DogfoodObservability.RecordExceptionAsync(
                "auth",
                "sign-out-revoke",
                ex,
                operation,
                new Dictionary<string, object?> { ["local_tokens_cleared"] = true });
            AuthStatus.Text = $"Local tokens cleared and account registration retained; remote revocation failed: {ex.Message}";
        }
    }

    private async Task RefreshRegistrationsAsync(string? selectClientId = null)
    {
        var registrations = await CredentialStore.ListRegistrationsAsync();
        AccountBox.ItemsSource = registrations;

        var targetClientId = selectClientId ?? _credential?.ClientId;
        if (!string.IsNullOrWhiteSpace(targetClientId))
        {
            AccountBox.SelectedItem = registrations.FirstOrDefault(x =>
                string.Equals(x.ClientId, targetClientId, StringComparison.Ordinal));
        }
        else
        {
            AccountBox.SelectedItem = null;
        }
    }

    private async void RefreshModels_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureCredentialAsync();
            await PopulateModelsAsync();
        }
        catch (Exception ex)
        {
            AuthStatus.Text = ex.Message;
        }
    }

    private async Task PopulateModelsAsync(string? correlationId = null)
    {
        if (_credential is null || !_credential.HasPlanUsage)
            return;

        var operation = DogfoodObservability.BeginOperation("model-discovery", correlationId);
        await DogfoodObservability.RecordOperationAsync("responses", "model-discovery", "start", operation);
        try
        {
            var models = await _core.ListModelsAsync(operation.CorrelationId);
            _credential = await CredentialStore.TryLoadAsync();
            ModelBox.ItemsSource = models;
            ModelBox.SelectedItem ??= models.FirstOrDefault();
            AuthStatus.Text = $"Agent Lab authorized; {models.Count} model(s) visible to this ChatGPT-plan grant.";
            await DogfoodObservability.RecordOperationAsync(
                "responses",
                "model-discovery",
                "success",
                operation,
                new Dictionary<string, object?> { ["model_count"] = models.Count });
        }
        catch (Exception ex)
        {
            await DogfoodObservability.RecordExceptionAsync("responses", "model-discovery", ex, operation);
            throw;
        }
    }

    private async void Attach_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this)
                ?? throw new InvalidOperationException("No desktop storage provider is available.");
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach image or file to Agent Lab",
                AllowMultiple = true
            });

            long totalBytes = _attachments.Sum(existing => EstimateDataUrlBytes(existing.Value));

            foreach (var file in files)
            {
                await using var stream = await file.OpenReadAsync();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                var bytes = memory.ToArray();
                totalBytes += bytes.LongLength;

                // This is the upstream Responses file-input limit, not an application policy.
                if (bytes.LongLength >= 50L * 1024 * 1024 || totalBytes > 50L * 1024 * 1024)
                    throw new InvalidOperationException(
                        "Responses file inputs require each file to be under 50 MB and all files combined to be at most 50 MB.");

                AddAttachmentBytes(file.Name, bytes);
            }

            RefreshAttachmentStatus();
        }
        catch (Exception ex)
        {
            AttachmentsStatus.Text = $"Attachment selection failed: {ex.Message}";
        }
    }

    private async void CaptureScreen_Click(object? sender, RoutedEventArgs e)
    {
        var descriptor = DesktopScreenCapture.Describe();
        if (!descriptor.Implemented)
        {
            AttachmentsStatus.Text =
                $"Screen capture is unavailable on this host. Adapter={descriptor.Adapter}; {descriptor.PermissionBoundary}";
            return;
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"suprachat-screen-{Guid.NewGuid():N}.png");

        try
        {
            AttachmentsStatus.Text =
                $"Capturing screen via {descriptor.Adapter}. {descriptor.PermissionBoundary}";
            var capturedPath = await DesktopScreenCapture.CaptureAsync(tempPath);
            var bytes = await File.ReadAllBytesAsync(capturedPath);
            AddAttachmentBytes("screen-capture.png", bytes);
            RefreshAttachmentStatus();
            AttachmentsStatus.Text += $" Screen captured via {descriptor.Adapter}.";
        }
        catch (Exception ex)
        {
            AttachmentsStatus.Text =
                $"Screen capture failed via {descriptor.Adapter}: {ex.Message}";
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private void ClearAttachments_Click(object? sender, RoutedEventArgs e)
    {
        _attachments.Clear();
        RefreshAttachmentStatus();
    }

    public async Task HandleStartupArgumentsAsync(IEnumerable<string> args)
    {
        var imported = 0;
        foreach (var raw in args.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (File.Exists(raw))
            {
                var bytes = await File.ReadAllBytesAsync(raw);
                AddAttachmentBytes(Path.GetFileName(raw), bytes);
                imported++;
                RootTabs.SelectedIndex = 1;
                continue;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "suprachat", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = QueryValue(uri, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                PromptBox.Text = text;
                RootTabs.SelectedIndex = 1;
            }
        }

        if (imported > 0)
        {
            RefreshAttachmentStatus();
            AuthStatus.Text = $"Imported {imported} file(s) from the desktop launch request.";
        }
    }

    private void AddAttachmentBytes(string name, byte[] bytes)
    {
        var totalBytes = _attachments.Sum(existing => EstimateDataUrlBytes(existing.Value)) + bytes.LongLength;
        if (bytes.LongLength >= 50L * 1024 * 1024 || totalBytes > 50L * 1024 * 1024)
            throw new InvalidOperationException(
                "Responses file inputs require each file to be under 50 MB and all files combined to be at most 50 MB.");

        var mime = MimeTypeFor(name);
        var kind = IsImageMime(mime) ? "image" : "file";
        _attachments.Add(new ResponseAttachment(
            kind,
            name,
            SiwcProtocol.ToDataUrl(mime, bytes),
            "auto"));
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var candidate = Uri.UnescapeDataString(pieces[0].Replace("+", " "));
            if (!string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                continue;

            return pieces.Length == 2
                ? Uri.UnescapeDataString(pieces[1].Replace("+", " "))
                : string.Empty;
        }

        return null;
    }

    private async void RunInterview_Click(object? sender, RoutedEventArgs e)
    {
        var timer = Stopwatch.StartNew();
        var operation = DogfoodObservability.BeginOperation("direct-responses-interview");
        await DogfoodObservability.RecordOperationAsync(
            "responses",
            "direct-interview",
            "start",
            operation,
            new Dictionary<string, object?>
            {
                ["attachment_count"] = _attachments.Count,
                ["web_search"] = WebSearchBox.IsChecked == true
            });
        try
        {
            await EnsureCredentialAsync(operation.CorrelationId);
            if (ModelBox.SelectedItem is not ModelChoice model)
                throw new InvalidOperationException("Select a model first.");

            OutputBox.Text = "";
            var result = await _responses.StreamAsync(
                _credential!.AccessToken,
                model.Slug,
                PromptBox.Text ?? string.Empty,
                _attachments,
                WebSearchBox.IsChecked == true,
                delta => Dispatcher.UIThread.Post(() => OutputBox.Text += delta));

            timer.Stop();
            if (!result.Completed)
                throw new InvalidOperationException("The stream ended without response.completed.");

            var receiptPath = await QualificationReceipts.WriteDirectAsync(
                model.Slug,
                _credential,
                completed: true,
                timer.Elapsed,
                result.Text.Length);

            AuthStatus.Text =
                $"Direct interview completed. Events={result.EventTypes.Count}; " +
                $"request_id={result.RequestId ?? "unknown"}; redacted receipt={Path.GetFileName(receiptPath)}";

            await DogfoodObservability.RecordOperationAsync(
                "responses",
                "direct-interview",
                "success",
                operation,
                new Dictionary<string, object?>
                {
                    ["model"] = model.Slug,
                    ["completed"] = result.Completed,
                    ["event_type_count"] = result.EventTypes.Count,
                    ["output_characters"] = result.Text.Length,
                    ["request_id"] = result.RequestId,
                    ["receipt_file"] = Path.GetFileName(receiptPath)
                });

            if (OperatingSystem.IsWindows() && !IsActive)
                DesktopNotificationService.TryNotify(this, "SupraChat", "Direct response completed.");
        }
        catch (Exception ex)
        {
            timer.Stop();
            await DogfoodObservability.RecordExceptionAsync(
                "responses",
                "direct-interview",
                ex,
                operation,
                new Dictionary<string, object?> { ["elapsed_ms"] = (long)timer.Elapsed.TotalMilliseconds });
            AuthStatus.Text = $"Direct interview failed: {ex.Message}";
        }
    }

    private async void SendRawResponses_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureCredentialAsync();
            if (ModelBox.SelectedItem is not ModelChoice model)
                throw new InvalidOperationException("Select a model first.");

            RawResponsesEventsBox.Text = "";
            var result = await _responses.StreamRawAsync(
                _credential!.AccessToken,
                model.Slug,
                RawResponsesBodyBox.Text ?? "{}",
                onDelta: null,
                onEvent: (type, payload) => Dispatcher.UIThread.Post(() =>
                    AppendRawResponseEvent(type, payload)));

            AuthStatus.Text =
                $"Raw Responses request completed={result.Completed}; events={result.EventTypes.Count}; " +
                $"request_id={result.RequestId ?? "unknown"}.";
        }
        catch (Exception ex)
        {
            AppendRawResponseEvent("ERROR", ex.ToString());
            AuthStatus.Text = $"Raw Responses request failed: {ex.Message}";
        }
    }

    private async void ConnectResponsesWebSocket_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureCredentialAsync();
            await StopResponsesWebSocketAsync();

            _responsesWebSocket = await ResponsesWebSocketClient.ConnectAsync(_credential!.AccessToken);
            _responsesWebSocketCts = new CancellationTokenSource();
            var socket = _responsesWebSocket;
            var cancellationToken = _responsesWebSocketCts.Token;
            _responsesWebSocketMonitorTask = Task.Run(
                () => MonitorResponsesWebSocketAsync(socket, cancellationToken),
                cancellationToken);

            AuthStatus.Text = "Responses WebSocket connected to the authenticated plan-sharing endpoint.";
        }
        catch (Exception ex)
        {
            AuthStatus.Text = $"Responses WebSocket connect failed: {ex.Message}";
        }
    }

    private async void DisconnectResponsesWebSocket_Click(object? sender, RoutedEventArgs e)
    {
        await StopResponsesWebSocketAsync();
        AuthStatus.Text = "Responses WebSocket disconnected.";
    }

    private async void SendResponsesWebSocketEvent_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureCredentialAsync();
            if (ModelBox.SelectedItem is not ModelChoice model)
                throw new InvalidOperationException("Select a model first.");

            if (_responsesWebSocket is not { IsConnected: true })
            {
                _responsesWebSocket = await ResponsesWebSocketClient.ConnectAsync(_credential!.AccessToken);
                _responsesWebSocketCts = new CancellationTokenSource();
                var socket = _responsesWebSocket;
                var cancellationToken = _responsesWebSocketCts.Token;
                _responsesWebSocketMonitorTask = Task.Run(
                    () => MonitorResponsesWebSocketAsync(socket, cancellationToken),
                    cancellationToken);
            }

            await _responsesWebSocket.SendRawAsync(
                ResponsesWebSocketEventBox.Text ?? "{}",
                model.Slug);
            AuthStatus.Text = "Responses WebSocket event sent.";
        }
        catch (Exception ex)
        {
            AppendResponsesWebSocketEvent($"SEND ERROR {ex}");
            AuthStatus.Text = $"Responses WebSocket send failed: {ex.Message}";
        }
    }

    private async Task MonitorResponsesWebSocketAsync(
        ResponsesWebSocketClient socket,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var raw in socket.ReadEventsAsync(cancellationToken))
            {
                Dispatcher.UIThread.Post(() => AppendResponsesWebSocketEvent(raw));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                AppendResponsesWebSocketEvent($"MONITOR ERROR {ex}"));
        }
    }

    private async void StartCodex_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var client = await EnsureCodexAsync();
            AuthStatus.Text =
                $"Codex app-server initialized (PID {client.ProcessId}); full protocol monitor is active.";
        }
        catch (Exception ex)
        {
            AuthStatus.Text = $"Codex app-server start failed: {ex.Message}";
        }
    }

    private async void RunCodexInterview_Click(object? sender, RoutedEventArgs e)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await EnsureCredentialAsync();
            if (ModelBox.SelectedItem is not ModelChoice model)
                throw new InvalidOperationException("Select a model first.");

            var client = await EnsureCodexAsync();
            OutputBox.Text = "";

            var result = await client.RunTextInterviewAsync(
                model.Slug,
                PromptBox.Text ?? string.Empty,
                delta => Dispatcher.UIThread.Post(() => OutputBox.Text += delta));

            timer.Stop();
            if (!result.Completed)
                throw new InvalidOperationException($"Codex turn completed with status {result.Status}.");

            var receiptPath = await QualificationReceipts.WriteCodexAsync(
                result.Model,
                _credential!,
                completed: true,
                timer.Elapsed,
                result.Text.Length);

            AuthStatus.Text =
                $"Codex interview completed. provider={result.ModelProvider}; thread={Short(result.ThreadId)}; " +
                $"turn={Short(result.TurnId)}; redacted receipt={Path.GetFileName(receiptPath)}";

            if (OperatingSystem.IsWindows() && !IsActive)
                DesktopNotificationService.TryNotify(this, "SupraChat", "Codex response completed.");
        }
        catch (Exception ex)
        {
            timer.Stop();
            AuthStatus.Text = $"Codex interview failed: {ex.Message}";
        }
    }

    private sealed record CodexCatalogChoice(string Name, string Kind, string State)
    {
        public override string ToString() => $"[{State} · {Kind}] {Name}";
    }

    private void RefreshCapabilityCatalog_Click(object? sender, RoutedEventArgs e) =>
        LoadCapabilityCatalog();

    private void LoadCapabilityCatalog()
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "oracles",
                "codex-capability-catalog-20261001.json");
            if (!File.Exists(path))
            {
                CapabilityCatalogStatus.Text = "Packaged Codex capability catalog is unavailable.";
                CodexMethodBox.ItemsSource = null;
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var packaged = root.GetProperty("packaged_runtime");
            var frontier = root.GetProperty("upstream_frontier");
            var stableNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var category in new[] { "client_requests", "server_requests", "server_notifications" })
            {
                foreach (var item in packaged.GetProperty(category).EnumerateArray())
                {
                    if (item.GetString() is { Length: > 0 } name)
                        stableNames.Add(name);
                }
            }

            var choices = new List<CodexCatalogChoice>();
            var allKnown = frontier.GetProperty("all_known");
            AddCatalogChoices(choices, allKnown.GetProperty("client_requests"), "client-request", stableNames);
            AddCatalogChoices(choices, allKnown.GetProperty("server_requests"), "server-request", stableNames);
            AddCatalogChoices(choices, allKnown.GetProperty("server_notifications"), "notification", stableNames);

            CodexMethodBox.ItemsSource = choices
                .OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToArray();

            var packagedTotal = packaged.GetProperty("counts").GetProperty("total").GetInt32();
            var frontierTotal = frontier.GetProperty("counts").GetProperty("total").GetInt32();
            CapabilityCatalogStatus.Text =
                $"Bundled Codex runtime: {packaged.GetProperty("version").GetString()} · " +
                $"{packagedTotal} protocol surfaces. Upstream frontier: {frontierTotal}. " +
                $"Frontier-only delta: {frontierTotal - packagedTotal}.";
        }
        catch (Exception ex)
        {
            CapabilityCatalogStatus.Text = $"Capability catalog could not be loaded: {ex.Message}";
            CodexMethodBox.ItemsSource = null;
        }
    }

    private static void AddCatalogChoices(
        ICollection<CodexCatalogChoice> output,
        JsonElement items,
        string kind,
        IReadOnlySet<string> stableNames)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetString() is not { Length: > 0 } name)
                continue;
            output.Add(new CodexCatalogChoice(
                name,
                kind,
                stableNames.Contains(name) ? "packaged" : "frontier-only"));
        }
    }

    private void CodexMethodBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CodexMethodBox.SelectedItem is not CodexCatalogChoice choice)
            return;

        if (choice.Kind == "client-request")
        {
            RawRpcMethodBox.Text = choice.Name;
            CapabilityCatalogStatus.Text =
                choice.State == "packaged"
                    ? $"{choice.Name} is present in the bundled Codex runtime and is ready for explicit RPC qualification."
                    : $"{choice.Name} is upstream-frontier only; it is tracked but is not expected in the bundled Codex 0.159.3 runtime.";
            return;
        }

        CapabilityCatalogStatus.Text =
            $"{choice.Name} is a {choice.Kind}. It is observed through the protocol monitor rather than invoked as a client request.";
    }

    private async void ListRealtimeVoices_Click(object? sender, RoutedEventArgs e) =>
        await RunReadOnlyCodexProbeAsync(
            "thread/realtime/listVoices",
            JsonSerializer.SerializeToElement(new { }));

    private async void ReadRemoteStatus_Click(object? sender, RoutedEventArgs e)
    {
        var result = await RunCodexProbeForResultAsync(
            "remoteControl/status/read",
            parameters: null,
            consequential: false);
        PopulateRemoteIdentity(result);
    }

    private async void RemoteEnable_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConsumeRemoteConfirmation("Enabling Codex Remote"))
            return;
        var result = await RunCodexProbeForResultAsync(
            "remoteControl/enable",
            JsonSerializer.SerializeToElement(new { ephemeral = false }),
            consequential: true);
        PopulateRemoteIdentity(result);
    }

    private async void RemoteDisable_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConsumeRemoteConfirmation("Disabling Codex Remote"))
            return;
        var result = await RunCodexProbeForResultAsync(
            "remoteControl/disable",
            JsonSerializer.SerializeToElement(new { ephemeral = false }),
            consequential: true);
        PopulateRemoteIdentity(result);
    }

    private async void RemotePairingStart_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConsumeRemoteConfirmation("Starting Codex Remote pairing"))
            return;

        var result = await RunCodexProbeForResultAsync(
            "remoteControl/pairing/start",
            JsonSerializer.SerializeToElement(new { manualCode = RemoteManualCodeBox.IsChecked == true }),
            consequential: true);

        if (result is { } value)
        {
            if (value.TryGetProperty("environmentId", out var environmentId) &&
                environmentId.ValueKind == JsonValueKind.String)
                RemoteEnvironmentBox.Text = environmentId.GetString();

            if (value.TryGetProperty("pairingCode", out var pairingCode) &&
                pairingCode.ValueKind == JsonValueKind.String)
                RemotePairingCodeBox.Text = pairingCode.GetString();

            if (value.TryGetProperty("manualPairingCode", out var manualPairingCode) &&
                manualPairingCode.ValueKind == JsonValueKind.String)
                RemoteManualPairingCodeBox.Text = manualPairingCode.GetString();
        }
    }

    private async void RemotePairingStatus_Click(object? sender, RoutedEventArgs e)
    {
        var result = await RunCodexProbeForResultAsync(
            "remoteControl/pairing/status",
            JsonSerializer.SerializeToElement(new
            {
                pairingCode = NullIfWhiteSpace(RemotePairingCodeBox.Text),
                manualPairingCode = NullIfWhiteSpace(RemoteManualPairingCodeBox.Text)
            }),
            consequential: false);

        if (result is { } value && value.TryGetProperty("claimed", out var claimed))
            AuthStatus.Text = $"Remote pairing claimed={claimed.GetBoolean()}.";
    }

    private async void RemoteClients_Click(object? sender, RoutedEventArgs e)
    {
        var environmentId = RequireText(RemoteEnvironmentBox.Text, "Remote environment ID");
        await RunCodexProbeForResultAsync(
            "remoteControl/client/list",
            JsonSerializer.SerializeToElement(new
            {
                environmentId,
                cursor = (string?)null,
                limit = (int?)null,
                order = (string?)null
            }),
            consequential: false);
    }

    private async void RemoteRevoke_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConsumeRemoteConfirmation("Revoking a Codex Remote client"))
            return;

        var environmentId = RequireText(RemoteEnvironmentBox.Text, "Remote environment ID");
        var clientId = RequireText(RemoteClientIdBox.Text, "Remote client ID");
        await RunCodexProbeForResultAsync(
            "remoteControl/client/revoke",
            JsonSerializer.SerializeToElement(new { environmentId, clientId }),
            consequential: true);
    }

    private bool ConsumeRemoteConfirmation(string operation)
    {
        if (RemoteConfirmBox.IsChecked != true)
        {
            RuntimeProbeOutputBox.Text =
                $"{operation} requires the explicit confirmation checkbox.";
            AuthStatus.Text = "Remote action not executed: confirmation is required.";
            return false;
        }

        RemoteConfirmBox.IsChecked = false;
        return true;
    }

    private void PopulateRemoteIdentity(JsonElement? result)
    {
        if (result is not { } value)
            return;

        if (value.TryGetProperty("environmentId", out var environmentId) &&
            environmentId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(environmentId.GetString()))
            RemoteEnvironmentBox.Text = environmentId.GetString();
    }

    private static string RequireText(string? value, string label) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"{label} is required.");

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async void ListPlugins_Click(object? sender, RoutedEventArgs e) =>
        await RunReadOnlyCodexProbeAsync(
            "plugin/list",
            JsonSerializer.SerializeToElement(new { }));

    private async void ListPermissionProfiles_Click(object? sender, RoutedEventArgs e) =>
        await RunReadOnlyCodexProbeAsync(
            "permissionProfile/list",
            JsonSerializer.SerializeToElement(new { }));

    private async void ListApps_Click(object? sender, RoutedEventArgs e) =>
        await RunReadOnlyCodexProbeAsync(
            "app/list",
            JsonSerializer.SerializeToElement(new { }));

    private async void ReadSandboxReadiness_Click(object? sender, RoutedEventArgs e) =>
        await RunReadOnlyCodexProbeAsync(
            "windowsSandbox/readiness",
            parameters: null);

    private void BrowserStatus_Click(object? sender, RoutedEventArgs e)
    {
        BrowserSnapshotBox.Text = JsonSerializer.Serialize(
            BrowserSession.Status(),
            new JsonSerializerOptions { WriteIndented = true });
    }

    private async void StartBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var url = BrowserUrlBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Enter an absolute HTTP or HTTPS URL.");

            await StopBrowserSessionAsync();
            BrowserSnapshotBox.Text = "Starting isolated browser…";
            var profile = Path.Combine(AppState.DirectoryPath, "browser-profile");
            _browserSession = await BrowserSession.StartPersistentAsync(profile, headless: false);
            var snapshot = await _browserSession.NavigateAndSnapshotAsync(url);
            BrowserSnapshotBox.Text = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions { WriteIndented = true });
            AuthStatus.Text = "Application-owned browser started with its persistent human profile. Semantic ARIA snapshot is available; Stop browser remains available.";
        }
        catch (Exception ex)
        {
            await StopBrowserSessionAsync();
            BrowserSnapshotBox.Text = ex.ToString();
            AuthStatus.Text = $"Clean-room browser start/inspect failed: {ex.Message}";
        }
    }

    private async void StopBrowser_Click(object? sender, RoutedEventArgs e)
    {
        await StopBrowserSessionAsync();
        BrowserSnapshotBox.Text = "Clean-room browser stopped.";
        AuthStatus.Text = "Clean-room browser stopped.";
    }

    private async Task StopBrowserSessionAsync()
    {
        if (_browserSession is null)
            return;

        await _browserSession.DisposeAsync();
        _browserSession = null;
    }

    private async Task RunReadOnlyCodexProbeAsync(string method, JsonElement? parameters) =>
        _ = await RunCodexProbeForResultAsync(method, parameters, consequential: false);

    private async Task<JsonElement?> RunCodexProbeForResultAsync(
        string method,
        JsonElement? parameters,
        bool consequential)
    {
        try
        {
            RuntimeProbeOutputBox.Text = $"Running {method}…";
            var client = await EnsureCodexAsync();
            var result = await client.RequestAsync(method, parameters);
            RuntimeProbeOutputBox.Text = PrettyJson(result);
            AuthStatus.Text = consequential
                ? $"Confirmed Codex action completed: {method}"
                : $"Read-only Codex probe completed: {method}";
            return result;
        }
        catch (Exception ex)
        {
            RuntimeProbeOutputBox.Text = ex.ToString();
            AuthStatus.Text = consequential
                ? $"Confirmed Codex action failed: {method}: {ex.Message}"
                : $"Read-only Codex probe failed: {method}: {ex.Message}";
            return null;
        }
    }

    private async void SendRawRpc_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var method = RawRpcMethodBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(method))
                throw new InvalidOperationException("Enter a Codex app-server method.");

            var client = await EnsureCodexAsync();
            var parameters = ParseOptionalJson(RawRpcParamsBox.Text);
            var result = await client.RequestAsync(method, parameters);
            RawRpcOutputBox.Text = PrettyJson(result);
            AuthStatus.Text = $"Codex RPC completed: {method}";
        }
        catch (Exception ex)
        {
            RawRpcOutputBox.Text = ex.ToString();
            AuthStatus.Text = $"Codex RPC failed: {ex.Message}";
        }
    }

    private async void RespondServerRequest_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_pendingServerRequests.Count == 0)
                throw new InvalidOperationException("No Codex server request is pending.");

            var request = _pendingServerRequests.Peek();
            if (request.Id is null)
                throw new InvalidOperationException("Pending server request has no id.");

            var result = ParseOptionalJson(ServerResponseBox.Text)
                ?? JsonSerializer.SerializeToElement(new { });

            var client = await EnsureCodexAsync();
            await client.RespondAsync(request.Id, result);
            _pendingServerRequests.Dequeue();
            ShowNextServerRequest();
            AppendProtocolEvent($"responded id={request.Id} method={request.Method}");
        }
        catch (Exception ex)
        {
            AuthStatus.Text = $"Server-request response failed: {ex.Message}";
        }
    }

    private async void RejectServerRequest_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_pendingServerRequests.Count == 0)
                throw new InvalidOperationException("No Codex server request is pending.");

            var request = _pendingServerRequests.Peek();
            if (request.Id is null)
                throw new InvalidOperationException("Pending server request has no id.");

            var client = await EnsureCodexAsync();
            await client.RespondErrorAsync(request.Id, -32000, "Rejected by SupraChat user.");
            _pendingServerRequests.Dequeue();
            ShowNextServerRequest();
            AppendProtocolEvent($"rejected id={request.Id} method={request.Method}");
        }
        catch (Exception ex)
        {
            AuthStatus.Text = $"Server-request rejection failed: {ex.Message}";
        }
    }

    private async Task EnsureCredentialAsync(string? correlationId = null)
    {
        var priorToken = _credential?.AccessToken;
        try
        {
            _credential = await _core.GetUsableCredentialAsync(
                requirePlanUsage: true,
                correlationId: correlationId);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("No local ChatGPT authorization", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Use Continue with ChatGPT first.", ex);
        }

        if (priorToken is not null &&
            !string.Equals(priorToken, _credential.AccessToken, StringComparison.Ordinal))
        {
            await StopCodexAsync();
            await StopResponsesWebSocketAsync();
        }
    }

    private async Task<CodexAppServerClient> EnsureCodexAsync()
    {
        await EnsureCredentialAsync();

        if (_codex is { IsRunning: true })
            return _codex;

        await StopCodexAsync();
        _codex = await CodexAppServerClient.StartAsync(_credential!.AccessToken);

        _codexEventsCts = new CancellationTokenSource();
        _codexEvents = _codex.SubscribeEvents();
        _codexMonitorTask = MonitorCodexAsync(_codexEvents, _codexEventsCts.Token);

        return _codex;
    }

    private async Task MonitorCodexAsync(
        CodexAppServerClient.CodexEventSubscription subscription,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var prefix = evt.Kind == "server-request"
                        ? $"SERVER REQUEST id={evt.Id} method={evt.Method}"
                        : $"EVENT {evt.Method}";
                    AppendProtocolEvent($"{prefix} {CompactJson(evt.Payload)}");

                    if (evt.Kind == "server-request")
                    {
                        _pendingServerRequests.Enqueue(evt);
                        ShowNextServerRequest();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                AppendProtocolEvent($"protocol monitor stopped: {ex.Message}"));
        }
    }

    private void ShowNextServerRequest()
    {
        if (_pendingServerRequests.Count == 0)
        {
            PendingServerRequestBox.Text = "";
            return;
        }

        var evt = _pendingServerRequests.Peek();
        PendingServerRequestBox.Text =
            $"id={evt.Id}{Environment.NewLine}method={evt.Method}{Environment.NewLine}{PrettyJson(evt.Payload)}";
    }

    private void AppendResponsesWebSocketEvent(string raw)
    {
        var compact = raw.Replace("\r", " ").Replace("\n", " ");
        if (compact.Length > 6000)
            compact = compact[..6000] + "…";

        var current = ResponsesWebSocketEventsBox.Text ?? "";
        var next = current + DateTimeOffset.Now.ToString("HH:mm:ss.fff") +
            " " + compact + Environment.NewLine;
        ResponsesWebSocketEventsBox.Text = next.Length <= 80000 ? next : next[^80000..];
    }

    private async Task StopResponsesWebSocketAsync()
    {
        var monitor = _responsesWebSocketMonitorTask;
        _responsesWebSocketMonitorTask = null;

        _responsesWebSocketCts?.Cancel();
        _responsesWebSocketCts?.Dispose();
        _responsesWebSocketCts = null;

        if (_responsesWebSocket is not null)
        {
            await _responsesWebSocket.DisposeAsync();
            _responsesWebSocket = null;
        }

        if (monitor is not null)
        {
            try
            {
                await monitor;
            }
            catch
            {
            }
        }
    }

    private void AppendRawResponseEvent(string type, string payload)
    {
        var compact = payload.Replace("\r", " ").Replace("\n", " ");
        if (compact.Length > 3000)
            compact = compact[..3000] + "…";

        var current = RawResponsesEventsBox.Text ?? "";
        var next = current + DateTimeOffset.Now.ToString("HH:mm:ss.fff") +
            " " + type + " " + compact + Environment.NewLine;
        RawResponsesEventsBox.Text = next.Length <= 60000 ? next : next[^60000..];
    }

    private void AppendProtocolEvent(string line)
    {
        var current = ProtocolEventsBox.Text ?? "";
        var next = current + DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine;
        ProtocolEventsBox.Text = next.Length <= 60000 ? next : next[^60000..];
    }

    private async Task StopCodexAsync()
    {
        var monitor = _codexMonitorTask;
        _codexMonitorTask = null;

        _codexEventsCts?.Cancel();
        _codexEventsCts?.Dispose();
        _codexEventsCts = null;

        if (_codexEvents is not null)
        {
            await _codexEvents.DisposeAsync();
            _codexEvents = null;
        }

        if (_codex is not null)
        {
            await _codex.DisposeAsync();
            _codex = null;
        }

        if (monitor is not null)
        {
            try
            {
                await monitor;
            }
            catch
            {
            }
        }

        _pendingServerRequests.Clear();
        ShowNextServerRequest();
    }

    private void RefreshAttachmentStatus()
    {
        if (_attachments.Count == 0)
        {
            AttachmentsStatus.Text = "No Agent Lab attachments selected.";
            return;
        }

        var images = _attachments.Count(x => x.Kind == "image");
        var files = _attachments.Count - images;
        AttachmentsStatus.Text = $"{_attachments.Count} attachment(s): {images} image(s), {files} file(s).";
    }

    private static JsonElement? ParseOptionalJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string PrettyJson(JsonElement element) =>
        JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    private static string CompactJson(JsonElement element)
    {
        var text = element.GetRawText().Replace("\r", " ").Replace("\n", " ");
        return text.Length <= 2000 ? text : text[..2000] + "…";
    }

    private static string MimeTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".tsv" => "text/tab-separated-values",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".rtf" => "application/rtf",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };

    private static bool IsImageMime(string mime) =>
        mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif";

    private static long EstimateDataUrlBytes(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0)
            return 0;
        var base64Length = value.Length - comma - 1;
        return base64Length * 3L / 4L;
    }

    private static string Short(string value) =>
        value.Length <= 12 ? value : value[..6] + "…" + value[^4..];

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Normal GUI close must not hide an unverified child termination.
        // Cancellation precedes destruction of the accessible Sessions UI.
        if (!_verifiedTerminalShutdown &&
            _workbenchTerminalSupervisor.HasOutstandingChildren)
        {
            e.Cancel = true;
            if (!_terminalShutdownInProgress)
            {
                _terminalShutdownInProgress = true;
                _ = VerifyTerminalShutdownThenCloseAsync();
            }
        }
        base.OnClosing(e);
    }

    private async Task VerifyTerminalShutdownThenCloseAsync()
    {
        try
        {
            await _workbenchTerminalSupervisor.DisposeAsync();
            _verifiedTerminalShutdown = true;
            Dispatcher.UIThread.Post(Close);
        }
        catch (InvalidOperationException)
        {
            _terminalShutdownInProgress = false;
            RootTabs.SelectedIndex = 3;
            WorkbenchTerminalStatus.Text =
                "Application close refused: the OS exit of a supervised child " +
                "was not verified. Inspect or stop the owned process, then close again. " +
                "Forcibly terminating SupraChat can leave descendants running.";
        }
        catch (Exception)
        {
            _terminalShutdownInProgress = false;
            RootTabs.SelectedIndex = 3;
            WorkbenchTerminalStatus.Text =
                "Application close blocked by an unexpected child cleanup error. " +
                "Inspect process state before retrying; no automatic override.";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _windowsCompanionHotkey?.Dispose();
            _windowsCompanionHotkey = null;
            StopBrowserSessionAsync().GetAwaiter().GetResult();
            StopCodexAsync().GetAwaiter().GetResult();
            StopResponsesWebSocketAsync().GetAwaiter().GetResult();
            _workbenchTerminalSupervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
        }

        base.OnClosed(e);
    }
}
