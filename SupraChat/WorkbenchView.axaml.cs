using Avalonia.Controls;
using Avalonia.Interactivity;
using SupraChat.Core;

namespace SupraChat;

public partial class WorkbenchView : UserControl
{
    // One local view, one accepted in-process W5 ledger. No independent W0 registry.
    private readonly SharedSessionLedger _ledger = new();
    private bool _refreshing;
    private string? _selectedId;

    public WorkbenchView()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh(string? notice = null)
    {
        var receipt = LedgerWorkbenchProjection.Read(_ledger, _selectedId);
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

    private void W0Sessions_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || W0Sessions.SelectedItem is not WorkbenchSession item)
            return;
        _selectedId = item.Id;
        W0Output.Text = string.Empty;
        Refresh("Selected a local session");
    }

    private void W0Create_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var view = LedgerWorkbenchProjection.Read(_ledger);
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
            var outcome = _ledger.Open("workbench-ui-" + Guid.NewGuid().ToString("N"),
                "human", title!, W0Conversation.Text, W0Workspace.Text);
            if (outcome.Applied && outcome.Session is not null)
            {
                _selectedId = outcome.Session.Id;
                W0Output.Text = string.Empty;
                Refresh("Local metadata created; no browser or terminal started");
            }
            else Refresh("Create refused: " + outcome.Status);
        }
        catch (ArgumentException)
        {
            W0Status.Text = "INVALID_SESSION_METADATA: nothing created.";
        }
    }

    private void W0Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedId is null || _ledger.Inspect(_selectedId) is not { } existing)
        {
            W0Status.Text = "SESSION_SELECTION_REQUIRED";
            return;
        }
        try
        {
            var outcome = _ledger.Change("workbench-ui-" + Guid.NewGuid().ToString("N"),
                "human", existing.Id, existing.Version, "set-title", W0NewTitle.Text);
            W0Output.Text = string.Empty;
            Refresh(outcome.Applied ? "Ledger title updated" : "Update refused: " + outcome.Status);
        }
        catch (ArgumentException)
        {
            W0Status.Text = "INVALID_SESSION_TITLE: no change.";
        }
    }

    private void Preview(string format)
    {
        var receipt = LedgerWorkbenchProjection.Read(_ledger, _selectedId);
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

    private void W0PreviewJson_Click(object? sender, RoutedEventArgs e) => Preview("json");
    private void W0PreviewMarkdown_Click(object? sender, RoutedEventArgs e) => Preview("markdown");
}
