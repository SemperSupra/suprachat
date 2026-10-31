using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SupraChat.Core;

namespace SupraChat;

public partial class WorkbenchView : UserControl
{
    private readonly IWorkbenchSessionAuthority _authority;
    private readonly LedgerLocalDrafts _drafts;
    private string? _w2ReviewId;
    private bool _refreshing;
    private string? _selectedId;
    private int _refreshGeneration;

    public WorkbenchView() : this(new ExperimentalLocalWorkbenchSessionAuthority())
    {
    }

    public WorkbenchView(IWorkbenchSessionAuthority authority)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _drafts = new LedgerLocalDrafts(_authority);
        InitializeComponent();
        _ = RefreshAsync();
    }

    // A remote owner may reply asynchronously. Never block the Avalonia UI thread
    // or apply an older response after another selection/refresh has begun.
    private async Task RefreshAsync(string? notice = null)
    {
        var generation = ++_refreshGeneration;
        LedgerWorkbenchReadReceipt receipt;
        try
        {
            receipt = await _authority.ReadAsync(_selectedId);
        }
        catch (Exception)
        {
            if (generation == _refreshGeneration)
            {
                W0Sessions.ItemsSource = null;
                W0Output.Text = string.Empty;
                W0Status.Text = "OWNER_UNAVAILABLE: session view cannot connect; no local fallback.";
            }
            return;
        }

        if (generation != _refreshGeneration) return;
        if (receipt.Snapshot is null)
        {
            W0Sessions.ItemsSource = null;
            W0Output.Text = string.Empty;
            W0Status.Text = "Session view refused: " + receipt.Status;
            return;
        }
        _selectedId = receipt.Snapshot.ActiveSessionId;
        if (_drafts.SelectedId != _selectedId)
        {
            try
            {
                await _drafts.SelectAsync(_selectedId);
            }
            catch (ArgumentException)
            {
                // Stale or uncommitted selection ignored
            }
            W2ClearReview();
        }
        RefreshDraftFields();
        _refreshing = true;
        try
        {
            W0Sessions.ItemsSource = receipt.Snapshot.Sessions;
            W0Sessions.SelectedItem = receipt.Snapshot.Sessions
                .FirstOrDefault(s => s.Id == _selectedId);
        }
        finally { _refreshing = false; }
        W0Status.Text = (notice ?? receipt.Status) +
            $"; sessions={receipt.Snapshot.Sessions.Count}/{receipt.Snapshot.MaxSessions}" +
            $"; collision warnings={receipt.Snapshot.Collisions.Count}." +
            " Metadata-only; no provider, terminal, actor identity or persistence.";
    }

    private async void W0Sessions_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || W0Sessions.SelectedItem is not WorkbenchSession item) return;
        _selectedId = item.Id;
        W2ClearReview();
        W0Output.Text = string.Empty;
        await RefreshAsync("Selected a local session");
    }

    private async void W0Create_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var view = await _authority.ReadAsync();
            if (view.Status != "OBSERVED" || view.Snapshot is null)
            {
                W0Status.Text = "Create refused: " + view.Status;
                return;
            }
            if (view.Snapshot.Sessions.Count >= SessionWorkbench.MaximumSessions)
            {
                W0Status.Text = "WORKBENCH_CAPACITY_REACHED: nothing created.";
                return;
            }
            var title = string.IsNullOrWhiteSpace(W0NewTitle.Text)
                ? "Local session " + (view.Snapshot.Sessions.Count + 1)
                : W0NewTitle.Text;
            var outcome = await _authority.CreateAsync("workbench-ui-" + Guid.NewGuid().ToString("N"),
                title!, W0Conversation.Text, W0Workspace.Text);
            if (outcome.Applied && outcome.Session is not null)
            {
                _selectedId = outcome.Session.Id;
                W2ClearReview();
                W0Output.Text = string.Empty;
                await RefreshAsync("Local metadata created; no browser or terminal started");
            }
            else await RefreshAsync("Create refused: " + outcome.Status);
        }
        catch (ArgumentException) { W0Status.Text = "INVALID_SESSION_METADATA: nothing created."; }
        catch (Exception) { W0Status.Text = "OUTCOME_UNKNOWN: create may have committed; reconnect and refresh before retrying."; }
    }

    private async void W0Rename_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _selectedId;
        if (selected is null)
        {
            W0Status.Text = "SESSION_SELECTION_REQUIRED";
            return;
        }
        try
        {
            var existing = await _authority.InspectAsync(selected);
            if (existing is null || _selectedId != selected)
            {
                W0Status.Text = "SESSION_SELECTION_STALE";
                return;
            }
            var outcome = await _authority.RenameAsync("workbench-ui-" + Guid.NewGuid().ToString("N"),
                existing.Id, existing.Version, W0NewTitle.Text ?? string.Empty);
            W0Output.Text = string.Empty;
            W2ClearReview();
            await RefreshAsync(outcome.Applied ? "Ledger title updated" : "Update refused: " + outcome.Status);
        }
        catch (ArgumentException) { W0Status.Text = "INVALID_SESSION_TITLE: no change."; }
        catch (Exception) { W0Status.Text = "OUTCOME_UNKNOWN: rename may have committed; reconnect and refresh before retrying."; }
    }

    private async Task PreviewAsync(string format)
    {
        try
        {
            var receipt = await _authority.ReadAsync(_selectedId);
            if (receipt.Status != "OBSERVED" || receipt.Snapshot is null)
            {
                W0Output.Text = string.Empty;
                W0Status.Text = "Preview refused: " + receipt.Status;
                return;
            }
            var preview = WorkbenchMetadataExport.Prepare(receipt.Snapshot, format);
            W0Output.Text = preview.Content;
            W0Status.Text = "Unsaved metadata preview. Titles, URLs and workspaces may contain sensitive details.";
        }
        catch (Exception)
        {
            W0Output.Text = string.Empty;
            W0Status.Text = "OWNER_UNAVAILABLE: preview refused.";
        }
    }

    private async void W0PreviewJson_Click(object? sender, RoutedEventArgs e) => await PreviewAsync("json");
    private async void W0PreviewMarkdown_Click(object? sender, RoutedEventArgs e) => await PreviewAsync("markdown");

    private async Task ExportFileAsync(string format)
    {
        try
        {
            var receipt = await _authority.ReadAsync(_selectedId);
            if (receipt.Status != "OBSERVED" || receipt.Snapshot is null)
            {
                W0Status.Text = "Export refused: " + receipt.Status;
                return;
            }
            var preview = WorkbenchMetadataExport.Prepare(receipt.Snapshot, format);
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null)
            {
                W0Status.Text = "Storage provider unavailable for file export.";
                return;
            }
            var ext = format == "json" ? "json" : "md";
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Export SupraChat session metadata ({format.ToUpperInvariant()})",
                SuggestedFileName = $"suprachat-workbench-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.{ext}",
                DefaultExtension = ext
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(preview.Content);
            W0Status.Text = $"Exported metadata ({format}) to {file.Name}. Contains no transcripts, credentials, or terminal output.";
        }
        catch (Exception ex)
        {
            W0Status.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void W0ExportJsonFile_Click(object? sender, RoutedEventArgs e) => await ExportFileAsync("json");
    private async void W0ExportMarkdownFile_Click(object? sender, RoutedEventArgs e) => await ExportFileAsync("markdown");

    private void W2ClearReview()
    {
        _w2ReviewId = null;
        _drafts.DiscardPending();
        W2ReviewOutput.Text = string.Empty;
    }

    private void RefreshDraftFields()
    {
        if (_drafts.SelectedId is null)
        {
            W2TerminalDraft.Text = string.Empty;
            W2ChatDraft.Text = string.Empty;
            return;
        }
        try
        {
            var buffers = _drafts.ReadSelected();
            W2TerminalDraft.Text = buffers.TerminalInput;
            W2ChatDraft.Text = buffers.ChatInput;
        }
        catch (InvalidOperationException)
        {
            W2TerminalDraft.Text = string.Empty;
            W2ChatDraft.Text = string.Empty;
        }
    }

    private async Task W2PrepareAsync(string direction)
    {
        W2ClearReview();
        try
        {
            var review = await _drafts.PrepareAsync(direction, W2DraftInput.Text ?? string.Empty);
            _w2ReviewId = review.ReviewId;
            W2ReviewOutput.Text = review.PreviewText;
            W2Status.Text = "Review valid for two minutes, one use, selected session/version only. " +
                "Nothing has been staged, submitted or executed.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            W2Status.Text = "Draft review refused; check selection, direction and 16 KiB UTF-8 bound.";
        }
    }

    private async void W2ReviewToTerminal_Click(object? sender, RoutedEventArgs e) =>
        await W2PrepareAsync("chat-to-terminal");

    private async void W2ReviewToChat_Click(object? sender, RoutedEventArgs e) =>
        await W2PrepareAsync("terminal-to-chat");

    private async void W2Stage_Click(object? sender, RoutedEventArgs e)
    {
        var id = _w2ReviewId;
        _w2ReviewId = null;
        if (id is null)
        {
            W2Status.Text = "STAGE_REFUSED: create and inspect a fresh review first.";
            return;
        }
        try
        {
            var receipt = await _drafts.StageReviewedAsync(id, W2DraftInput.Text ?? string.Empty);
            W2DraftInput.Text = string.Empty;
            W2ReviewOutput.Text = string.Empty;
            RefreshDraftFields();
            W2Status.Text = receipt.Staged
                ? "Staged only to this session's local inert text buffer. Nothing transmitted or executed."
                : "Stage refused.";
        }
        catch (InvalidOperationException)
        {
            W2ClearReview();
            W2Status.Text = "STAGE_REFUSED: review changed, expired, replayed, or ledger version changed.";
        }
    }

    private async void W2Discard_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedId is null)
        {
            W2Status.Text = "Select a local session before discarding.";
            return;
        }
        _drafts.Forget(_selectedId);
        try
        {
            await _drafts.SelectAsync(_selectedId);
        }
        catch (ArgumentException)
        {
            // selection cleared
        }
        W2ClearReview();
        W2DraftInput.Text = string.Empty;
        RefreshDraftFields();
        W2Status.Text = "Local session draft buffers discarded; no provider or terminal effects.";
    }
}
