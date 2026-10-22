using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SupraChat.SessionCore;

namespace SupraChat;

public partial class MainWindow
{
    // A synthetic UI participant is local to this window, and intentionally
    // DOES NOT have authenticated agent delegation or ChatGPT send permission.
    private readonly string _sharedHumanActorId = "ui-human-" + Guid.NewGuid().ToString("N");
    private sealed record SharedSessionRow(string Id, string Display, long Revision);

    private SharedSessionClient SharedClient() =>
        new(SharedSessionPipeInput.Text?.Trim() ?? string.Empty);

    private static string ShortSessionId(string id) => id[..Math.Min(12, id.Length)];

    private async Task RefreshSharedSessionListAsync(string? selectId = null)
    {
        var client = SharedClient();
        var reply = await client.RequestAsync(new SessionRequest("session/list"));
        if (!reply.Ok || reply.Result is null || reply.Result.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(reply.Error ?? "INVALID_SHARED_SESSION_LIST");

        var rows = reply.Result.Value.EnumerateArray()
            .Select(item =>
            {
                var id = item.GetProperty("id").GetString()!;
                var title = item.GetProperty("title").GetString()!;
                var revision = item.GetProperty("revision").GetInt64();
                return new SharedSessionRow(id,
                    $"{title} [id={ShortSessionId(id)}, revision={revision}]", revision);
            }).ToArray();
        var preserved = selectId ?? (SharedSessionList.SelectedItem as SharedSessionRow)?.Id;
        SharedSessionList.ItemsSource = rows;
        SharedSessionList.SelectedItem = rows.FirstOrDefault(s => s.Id == preserved);
        SharedSessionStatus.Text =
            $"Shared synthetic host connected: {rows.Length} sessions. " +
            "This view is independent of the local preview workbench. " +
            "No ChatGPT account, terminal or other provider is connected.";
    }

    private async void SharedSessionRefresh_Click(object? sender, RoutedEventArgs e)
    {
        try { await RefreshSharedSessionListAsync(); }
        catch (Exception)
        {
            SharedSessionStatus.Text =
                "Local shared-session host unavailable or reply invalid. " +
                "No local fallback or remote action was attempted.";
        }
    }

    private async void SharedSessionCreate_Click(object? sender, RoutedEventArgs e)
    {
        var title = SharedSessionTitleInput.Text ?? string.Empty;
        // A new operation ID is generated only for an explicit human click.
        // A lost ACK must NOT be blindly retried with a different ID.
        var operationId = "ui-create-" + Guid.NewGuid().ToString("N");
        try
        {
            var reply = await SharedClient().RequestAsync(new SessionRequest(
                "session/create", ActorId: _sharedHumanActorId, Audience: "human",
                Title: title, OperationId: operationId));
            if (!reply.Ok)
            {
                SharedSessionStatus.Text = $"Create refused: {reply.Error}. No provider action.";
                return;
            }
            var id = reply.Result!.Value.GetProperty("id").GetString()!;
            await RefreshSharedSessionListAsync(id);
            SharedSessionStatus.Text = $"Created shared synthetic session {ShortSessionId(id)}. " +
                "Other same-user participants can observe this ID through the host.";
        }
        catch (Exception)
        {
            SharedSessionStatus.Text =
                "Shared create outcome uncertain. Refresh the shared session list to reconcile; " +
                "do not blindly create again. No provider was contacted.";
        }
    }

    private async void SharedSessionJoin_Click(object? sender, RoutedEventArgs e)
    {
        if (SharedSessionList.SelectedItem is not SharedSessionRow selected)
        {
            SharedSessionStatus.Text = "Select a shared session to join.";
            return;
        }
        try
        {
            var reply = await SharedClient().RequestAsync(new SessionRequest(
                "session/join", SessionId: selected.Id, ActorId: _sharedHumanActorId,
                Audience: "human"));
            if (!reply.Ok)
            {
                SharedSessionStatus.Text = $"Join refused: {reply.Error}.";
                return;
            }
            await RefreshSharedSessionListAsync(selected.Id);
            await ShowSharedSessionDetailsAsync(selected.Id);
        }
        catch (Exception)
        {
            SharedSessionStatus.Text =
                "Join outcome unknown. Refresh and inspect before retrying.";
        }
    }

    private async void SharedSessionDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (SharedSessionList.SelectedItem is not SharedSessionRow selected)
        {
            SharedSessionStatus.Text = "Select a shared session first.";
            return;
        }
        try { await ShowSharedSessionDetailsAsync(selected.Id); }
        catch (Exception)
        {
            SharedSessionStatus.Text = "Shared host read failed; no fallback to stale local state.";
        }
    }

    private async Task ShowSharedSessionDetailsAsync(string id)
    {
        var client = SharedClient();
        var snapshot = await client.RequestAsync(new SessionRequest("session/read", SessionId: id));
        var history = await client.RequestAsync(new SessionRequest("session/events", SessionId: id));
        if (!snapshot.Ok || !history.Ok || snapshot.Result is null || history.Result is null)
            throw new InvalidOperationException("SHARED_SESSION_READ_FAILED");
        var current = snapshot.Result.Value;
        var events = history.Result.Value.GetProperty("events");
        var output = new StringBuilder();
        output.AppendLine($"Session {ShortSessionId(id)}, revision {current.GetProperty("revision").GetInt64()}");
        output.AppendLine($"Quarantined: {current.GetProperty("quarantined").GetBoolean()}");
        output.AppendLine("Events, ordered by shared-session revision:");
        foreach (var ev in events.EnumerateArray())
        {
            output.Append(ev.GetProperty("revision").GetInt64());
            output.Append(" | ");
            output.Append(ev.GetProperty("audience").GetString());
            output.Append(" | ");
            output.Append(ev.GetProperty("kind").GetString());
            output.Append(" | ");
            output.AppendLine(ev.GetProperty("status").GetString());
        }
        SharedSessionEventsView.Text = output.ToString();
        SharedSessionStatus.Text =
            "Shared-session state refreshed from the host. Human and machine clients " +
            "see the same recorded events; no ChatGPT prompts or terminal commands.";
    }

    private async void SharedSessionAppend_Click(object? sender, RoutedEventArgs e)
    {
        if (SharedSessionList.SelectedItem is not SharedSessionRow selected)
        {
            SharedSessionStatus.Text = "Select a shared session first.";
            return;
        }
        try
        {
            var reply = await SharedClient().RequestAsync(new SessionRequest(
                "session/append", SessionId: selected.Id,
                ActorId: _sharedHumanActorId, Audience: "human",
                OperationId: "ui-note-" + Guid.NewGuid().ToString("N"),
                ExpectedRevision: selected.Revision,
                Kind: "NOTE", Detail: SharedSessionNoteInput.Text ?? string.Empty));
            if (!reply.Ok)
            {
                SharedSessionStatus.Text = $"Append refused: {reply.Error}. " +
                    "Refresh for the current revision or join this session.";
                return;
            }
            SharedSessionNoteInput.Text = string.Empty;
            await RefreshSharedSessionListAsync(selected.Id);
            await ShowSharedSessionDetailsAsync(selected.Id);
        }
        catch (Exception)
        {
            SharedSessionStatus.Text =
                "Note outcome uncertain. Inspect the session events before taking another action.";
        }
    }
}
