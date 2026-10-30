using Avalonia.Controls;
using Avalonia.Interactivity;
using SupraChat.Core;

namespace SupraChat;

public partial class MainWindow
{
    // Single in-process workbench metadata authority; no W6 host or actor identity claim.
    // The local selected row is a presentation pointer, not a second session registry.
    private readonly SharedSessionLedger _w0Ledger = new();
    private string? _w0SelectedId;
    private bool _w0Refreshing;

    private void W0Tab_Loaded(object? sender, RoutedEventArgs e) => RefreshW0();

    private void RefreshW0(string? message = null)
    {
        var observed = LedgerWorkbenchProjection.Read(_w0Ledger, _w0SelectedId);
        if (observed.Snapshot is null)
        {
            W0Status.Text = "Session view refused: " + observed.Status;
            W0Sessions.ItemsSource = null;
            W0Output.Text = string.Empty;
            return;
        }
        _w0SelectedId = observed.Snapshot.ActiveSessionId;
        _w0Refreshing = true;
        try
        {
            W0Sessions.ItemsSource = observed.Snapshot.Sessions;
            W0Sessions.SelectedItem = observed.Snapshot.Sessions
                .FirstOrDefault(item => item.Id == _w0SelectedId);
        }
        finally { _w0Refreshing = false; }
        W0Status.Text = (message ?? observed.Status) +
            $"; sessions={observed.Snapshot.Sessions.Count}/{observed.Snapshot.MaxSessions}" +
            $"; collisions={observed.Snapshot.Collisions.Count}." +
            " Local metadata only. No terminal execution, provider send or durable recovery.";
    }

    private void W0Sessions_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_w0Refreshing || W0Sessions.SelectedItem is not WorkbenchSession item)
            return;
        _w0SelectedId = item.Id;
        W0Output.Text = string.Empty;
        RefreshW0("Session selected; no external effect");
    }

    private void W0Create_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (LedgerWorkbenchProjection.Read(_w0Ledger).Snapshot?.Sessions.Count >=
                SessionWorkbench.MaximumSessions)
            {
                W0Status.Text = "WORKBENCH_CAPACITY_REACHED: no session created.";
                return;
            }
            var title = string.IsNullOrWhiteSpace(W0NewTitle.Text)
                ? "Local session " + (_w0Ledger.InspectAll().Sessions.Count + 1)
                : W0NewTitle.Text;
            var result = _w0Ledger.Open("local-w0-" + Guid.NewGuid().ToString("N"),
                "human", title!, W0Conversation.Text, W0Workspace.Text);
            if (result.Applied && result.Session is not null)
            {
                _w0SelectedId = result.Session.Id;
                W0Output.Text = string.Empty;
                RefreshW0("Created a local metadata session; browser and terminal remain untouched");
            }
            else RefreshW0("Create refused: " + result.Status);
        }
        catch (ArgumentException)
        {
            W0Status.Text = "INVALID_SESSION_METADATA: nothing created.";
        }
    }

    private void W0Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (_w0SelectedId is null || _w0Ledger.Inspect(_w0SelectedId) is not { } state)
        {
            W0Status.Text = "SESSION_SELECTION_REQUIRED";
            return;
        }
        try
        {
            var result = _w0Ledger.Change("local-w0-" + Guid.NewGuid().ToString("N"),
                "human", state.Id, state.Version, "set-title", W0NewTitle.Text);
            W0Output.Text = string.Empty;
            RefreshW0(result.Applied ? "Title updated in local ledger" : result.Status);
        }
        catch (ArgumentException)
        {
            W0Status.Text = "INVALID_SESSION_TITLE: no metadata changed.";
        }
    }

    private void W0PreviewExport(string format)
    {
        var result = LedgerWorkbenchProjection.Read(_w0Ledger, _w0SelectedId);
        if (result.Status != "OBSERVED" || result.Snapshot is null)
        {
            W0Output.Text = string.Empty;
            W0Status.Text = "Export preview refused: " + result.Status;
            return;
        }
        var preview = WorkbenchMetadataExport.Prepare(result.Snapshot, format);
        W0Output.Text = preview.Content;
        W0Status.Text = "Metadata preview only; requires user review. No file written. " +
            "Titles, URLs and workspace references may themselves be sensitive.";
    }

    private void W0PreviewJson_Click(object? sender, RoutedEventArgs e) =>
        W0PreviewExport("json");
    private void W0PreviewMarkdown_Click(object? sender, RoutedEventArgs e) =>
        W0PreviewExport("markdown");
}
