using Avalonia.Controls;
using Avalonia.Interactivity;
using SupraChat.Core;

namespace SupraChat;

public partial class WorkbenchView : UserControl
{
    private readonly IWorkbenchSessionAuthority _authority;
    private bool _refreshing;
    private string? _selectedId;
    private int _refreshGeneration;

    public WorkbenchView(IWorkbenchSessionAuthority authority)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
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
                W0Output.Text = string.Empty;
                await RefreshAsync("Local metadata created; no browser or terminal started");
            }
            else await RefreshAsync("Create refused: " + outcome.Status);
        }
        catch (ArgumentException) { W0Status.Text = "INVALID_SESSION_METADATA: nothing created."; }
        catch (Exception) { W0Status.Text = "OWNER_UNAVAILABLE: no session created."; }
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
            await RefreshAsync(outcome.Applied ? "Ledger title updated" : "Update refused: " + outcome.Status);
        }
        catch (ArgumentException) { W0Status.Text = "INVALID_SESSION_TITLE: no change."; }
        catch (Exception) { W0Status.Text = "OWNER_UNAVAILABLE: no local fallback."; }
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
}
