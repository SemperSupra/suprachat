using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SupraChat.Core;

namespace SupraChat;

public partial class WorkbenchView : UserControl
{
    private readonly IWorkbenchSessionAuthority _authority;
    private readonly LedgerLocalDrafts _drafts;
    private readonly SessionTerminalHost _terminalHost = new();
    private string? _w2ReviewId;
    private bool _refreshing;
    private string? _selectedId;
    private int _refreshGeneration;
    private IReadOnlyList<CodeSnippet> _extractedSnippets = Array.Empty<CodeSnippet>();

    public WorkbenchView() : this(CreateDefaultAuthority())
    {
    }

    private static IWorkbenchSessionAuthority CreateDefaultAuthority()
    {
        var portText = Environment.GetEnvironmentVariable("SUPRACHAT_W6_GUI_PORT");
        if (portText is null)
            return new ExperimentalLocalWorkbenchSessionAuthority();
        if (Environment.GetEnvironmentVariable("SUPRACHAT_W6_DISPOSABLE_ONLY") != "yes" ||
            !int.TryParse(portText, out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("W6_SYNTHETIC_GUI_PORT_ADMISSION_REFUSED");
        return new W6SyntheticWorkbenchSessionAuthority(port);
    }

    public WorkbenchView(IWorkbenchSessionAuthority authority)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _drafts = new LedgerLocalDrafts(_authority);
        InitializeComponent();

        _terminalHost.OutputReceived += OnTerminalOutputReceived;
        _terminalHost.StateChanged += OnTerminalStateChanged;

        _ = RefreshAsync();
    }

    private void OnTerminalOutputReceived(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (line == "\x1Bc")
            {
                TerminalConsoleOutput.Text = string.Empty;
                return;
            }
            TerminalConsoleOutput.Text += line + Environment.NewLine;
            TerminalConsoleOutput.CaretIndex = TerminalConsoleOutput.Text?.Length ?? 0;
        });
    }

    private void OnTerminalStateChanged(bool isRunning)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TerminalRunButton.IsEnabled = !isRunning;
            TerminalStopButton.IsEnabled = isRunning;
            TerminalStatusText.Text = isRunning ? "⏳ Shell Running..." : "🟢 Shell Ready";
        });
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
            W0CollisionBorder.IsVisible = false;
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
            var active = receipt.Snapshot.Sessions.FirstOrDefault(s => s.Id == _selectedId);
            W0Sessions.SelectedItem = active;

            // Bind terminal working directory to active session workspace if available
            if (active is not null && !string.IsNullOrWhiteSpace(active.WorkspaceKey))
            {
                _terminalHost.WorkingDirectory = active.WorkspaceKey;
                TerminalWorkingDirectory.Text = "Working Dir: " + active.WorkspaceKey;
                WorkbenchPairStatusText.Text = $"💻 Session: {active.Title}";
            }
            else
            {
                _terminalHost.WorkingDirectory = Directory.GetCurrentDirectory();
                TerminalWorkingDirectory.Text = "Working Dir: " + Directory.GetCurrentDirectory();
                WorkbenchPairStatusText.Text = "💻 Local Execution Bound";
            }
        }
        finally { _refreshing = false; }

        if (receipt.Snapshot.Collisions.Count > 0)
        {
            var collisionLines = receipt.Snapshot.Collisions.Select(c =>
                $"{c.Type}: {c.LeftSessionId} ↔ {c.RightSessionId}" +
                (string.IsNullOrWhiteSpace(c.Detail) ? "" : $" ({c.Detail})"));
            W0CollisionText.Text = "Collision alerts detected:\n" + string.Join("\n", collisionLines);
            W0CollisionBorder.IsVisible = true;
        }
        else
        {
            W0CollisionText.Text = string.Empty;
            W0CollisionBorder.IsVisible = false;
        }

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

    private async void W0Close_Click(object? sender, RoutedEventArgs e)
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
            _drafts.Forget(selected);
            W2ClearReview();
            var outcome = await _authority.CloseAsync("workbench-ui-" + Guid.NewGuid().ToString("N"),
                existing.Id, existing.Version);
            _selectedId = null;
            W0Output.Text = string.Empty;
            await RefreshAsync(outcome.Applied ? "Session archived; draft buffers cleared." : "Close refused: " + outcome.Status);
        }
        catch (Exception) { W0Status.Text = "OUTCOME_UNKNOWN: close may have committed; reconnect and refresh before retrying."; }
    }

    private async void W0Restore_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var receipt = await _authority.ReadAsync(_selectedId);
            if (receipt.Snapshot?.ArchivedSessions is null || receipt.Snapshot.ArchivedSessions.Count == 0)
            {
                W0Status.Text = "NO_ARCHIVED_SESSIONS";
                return;
            }
            var toRestore = receipt.Snapshot.ArchivedSessions.Last();
            var existing = await _authority.InspectAsync(toRestore.Id);
            if (existing is null)
            {
                W0Status.Text = "SESSION_SELECTION_STALE";
                return;
            }
            var outcome = await _authority.RestoreAsync("workbench-ui-" + Guid.NewGuid().ToString("N"),
                existing.Id, existing.Version);
            if (outcome.Applied && outcome.Session is not null)
            {
                _selectedId = outcome.Session.Id;
                await RefreshAsync("Session restored to active list");
            }
            else
            {
                await RefreshAsync("Restore refused: " + outcome.Status);
            }
        }
        catch (Exception) { W0Status.Text = "OUTCOME_UNKNOWN: restore may have committed; reconnect and refresh before retrying."; }
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

    private void W2DraftInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = W2DraftInput.Text ?? string.Empty;
        var hasSensitive = TerminalTransferBridge.ContainsSensitiveTokens(text);
        if (hasSensitive)
        {
            DraftSafetyBadge.Background = new SolidColorBrush(Color.Parse("#FFEBEE"));
            DraftSafetyBadge.BorderBrush = new SolidColorBrush(Color.Parse("#EF9A9A"));
            DraftSafetyText.Text = "⚠️ Secret / Token Detected!";
            DraftSafetyText.Foreground = new SolidColorBrush(Color.Parse("#C62828"));
        }
        else
        {
            DraftSafetyBadge.Background = new SolidColorBrush(Color.Parse("#E8F5E9"));
            DraftSafetyBadge.BorderBrush = new SolidColorBrush(Color.Parse("#A5D6A7"));
            DraftSafetyText.Text = "🛡️ Secret Scan Clean";
            DraftSafetyText.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
        }
    }

    private void ExtractSnippets_Click(object? sender, RoutedEventArgs e)
    {
        var text = W2DraftInput.Text ?? string.Empty;
        _extractedSnippets = TerminalTransferBridge.ExtractCodeSnippets(text);
        if (_extractedSnippets.Count == 0)
        {
            SnippetsContainer.IsVisible = false;
            W2Status.Text = "No markdown code snippets found (use ```lang ... ``` blocks).";
            return;
        }

        SnippetsList.ItemsSource = _extractedSnippets.Select((s, i) =>
            $"Snippet #{i + 1} [{s.Language}]: {s.Lines.Count} line(s) - \"{s.Lines.FirstOrDefault() ?? ""}\"").ToList();
        SnippetsContainer.IsVisible = true;
        W2Status.Text = $"Extracted {_extractedSnippets.Count} code snippet block(s). Select to inject or copy.";
    }

    private void SnippetsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Selection updated
    }

    private void InjectSnippetToTerminal_Click(object? sender, RoutedEventArgs e)
    {
        var idx = SnippetsList.SelectedIndex;
        if (idx < 0 || idx >= _extractedSnippets.Count)
        {
            W2Status.Text = "Select a code snippet first.";
            return;
        }
        var snippet = _extractedSnippets[idx];
        TerminalCommandInput.Text = string.Join(" ", snippet.Lines.Select(l => l.Trim()));
        W2Status.Text = $"Injected Snippet #{idx + 1} into terminal command line.";
    }

    private async void CopySnippet_Click(object? sender, RoutedEventArgs e)
    {
        var idx = SnippetsList.SelectedIndex;
        if (idx < 0 || idx >= _extractedSnippets.Count)
        {
            W2Status.Text = "Select a code snippet first.";
            return;
        }
        var snippet = _extractedSnippets[idx];
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not null)
        {
            await top.Clipboard.SetTextAsync(snippet.Content);
            W2Status.Text = $"Copied Snippet #{idx + 1} to clipboard.";
        }
    }

    private async Task W2PrepareAsync(string direction)
    {
        W2ClearReview();
        try
        {
            var rawText = W2DraftInput.Text ?? string.Empty;
            // Clean ANSI escape codes and redact sensitive tokens before staging
            var sanitized = TerminalTransferBridge.StripAnsi(rawText);
            if (TerminalTransferBridge.ContainsSensitiveTokens(sanitized))
            {
                sanitized = TerminalTransferBridge.RedactSensitiveTokens(sanitized);
            }

            var review = await _drafts.PrepareAsync(direction, sanitized);
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
            var receipt = await _drafts.StageReviewedAsync(id, W2ReviewOutput.Text ?? string.Empty);
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

    // ================= PAIRED TERMINAL ACTIONS =================

    private async void TerminalRun_Click(object? sender, RoutedEventArgs e)
    {
        await ExecuteTerminalCommandAsync();
    }

    private async void TerminalCommandInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ExecuteTerminalCommandAsync();
        }
    }

    private async Task ExecuteTerminalCommandAsync()
    {
        var cmd = TerminalCommandInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) return;

        TerminalCommandInput.Text = string.Empty;
        try
        {
            var exitCode = await _terminalHost.ExecuteCommandAsync(cmd);
            TerminalStatusText.Text = $"🟢 Exit: {exitCode}";
        }
        catch (Exception ex)
        {
            TerminalStatusText.Text = "⚠️ Error: " + ex.Message;
        }
    }

    private void TerminalStop_Click(object? sender, RoutedEventArgs e)
    {
        _terminalHost.Stop();
        TerminalStatusText.Text = "⏹ Stopped by operator";
    }

    private void TerminalClear_Click(object? sender, RoutedEventArgs e)
    {
        _terminalHost.ClearScrollback();
    }

    private void TerminalPasteDraft_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(W2TerminalDraft.Text))
        {
            TerminalCommandInput.Text = W2TerminalDraft.Text;
            TerminalStatusText.Text = "Pasted staged terminal draft into command line.";
        }
        else
        {
            TerminalStatusText.Text = "No staged terminal draft to paste.";
        }
    }

    private void TerminalSendLastOutput_Click(object? sender, RoutedEventArgs e)
    {
        var last = _terminalHost.LastOutput;
        if (string.IsNullOrWhiteSpace(last))
        {
            W2Status.Text = "No command output to transfer.";
            return;
        }

        var clean = TerminalTransferBridge.ExtractCleanLastCommandOutput(last);
        clean = TerminalTransferBridge.StripAnsi(clean);
        if (TerminalTransferBridge.ContainsSensitiveTokens(clean))
        {
            clean = TerminalTransferBridge.RedactSensitiveTokens(clean);
        }

        var formatted = TerminalTransferBridge.FormatTerminalOutputForChat(_terminalHost.LastCommand ?? "shell", clean);
        W2DraftInput.Text = formatted;
        W2Status.Text = "Transferred clean last command output to draft input for chat review.";
    }

    private void TerminalSendErrorTrace_Click(object? sender, RoutedEventArgs e)
    {
        var last = _terminalHost.LastOutput;
        if (string.IsNullOrWhiteSpace(last))
        {
            W2Status.Text = "No command output to extract error trace from.";
            return;
        }

        var errorTrace = TerminalTransferBridge.ExtractErrorTrace(last);
        if (string.IsNullOrWhiteSpace(errorTrace))
        {
            W2Status.Text = "No error trace patterns detected in last output.";
            return;
        }

        errorTrace = TerminalTransferBridge.StripAnsi(errorTrace);
        var sb = new StringBuilder();
        sb.AppendLine($"Error trace from command `{_terminalHost.LastCommand}`:");
        sb.AppendLine("```");
        sb.AppendLine(errorTrace);
        sb.AppendLine("```");
        sb.AppendLine("Please diagnose this error trace and provide a fix.");

        W2DraftInput.Text = sb.ToString();
        W2Status.Text = "Extracted error trace and formatted for ChatGPT debugging draft.";
    }
}
