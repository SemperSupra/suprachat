using System.Text;
namespace SupraChat.Core;

/// <summary>
/// W0 non-persistent, non-executing semantics for one conversation plus its
/// associated terminal slot. Neither browser submission nor terminal commands
/// can be invoked through this model. No account or credential state is stored.
/// </summary>
public sealed class SessionWorkbench
{
    public const string Schema = "suprachat-session-workbench/v1";
    public const int MaximumSessions = 16;

    private readonly List<WorkbenchSession> _sessions = new();
    private string? _activeSessionId;

    public WorkbenchSnapshot Snapshot()
    {
        var collisions = new List<WorkbenchCollision>();
        for (var i = 0; i < _sessions.Count; i++)
        {
            for (var j = i + 1; j < _sessions.Count; j++)
            {
                var left = _sessions[i];
                var right = _sessions[j];
                if (left.ConversationUrl is not null &&
                    string.Equals(left.ConversationUrl, right.ConversationUrl, StringComparison.Ordinal))
                    collisions.Add(new("DUPLICATE_CONVERSATION", left.Id, right.Id));
                if (left.WorkspaceKey is not null &&
                    string.Equals(left.WorkspaceKey, right.WorkspaceKey, StringComparison.OrdinalIgnoreCase))
                    collisions.Add(new("SHARED_WORKSPACE", left.Id, right.Id));
            }
        }
        return new(Schema, _activeSessionId, _sessions.ToArray(), collisions,
            MaximumSessions, false, false);
    }

    public WorkbenchSession Open(string? title = null, string? conversationUrl = null, string? workspaceKey = null)
    {
        if (_sessions.Count >= MaximumSessions)
            throw new InvalidOperationException("MAX_SESSIONS_REACHED");

        string? canonicalUrl = null;
        if (!string.IsNullOrWhiteSpace(conversationUrl))
        {
            var inspection = ChatGptControlContract.Inspect(conversationUrl);
            if (!inspection.Recognized || inspection.CanonicalUrl is null)
                throw new ArgumentException("UNRECOGNIZED_CONVERSATION_URL", nameof(conversationUrl));
            canonicalUrl = inspection.CanonicalUrl;
        }
        var workspace = NormalizeWorkspace(workspaceKey);
        var sanitizedTitle = NormalizeTitle(title, _sessions.Count + 1);
        var item = new WorkbenchSession(
            Guid.NewGuid().ToString("N"), sanitizedTitle, canonicalUrl, workspace,
            "NOT_CONNECTED", TerminalExecutionPermitted: false, ChatSubmissionPermitted: false);
        _sessions.Add(item);
        _activeSessionId = item.Id;
        return item;
    }

    public bool Select(string sessionId)
    {
        if (!_sessions.Any(s => s.Id == sessionId)) return false;
        _activeSessionId = sessionId;
        return true;
    }

    public bool Close(string sessionId)
    {
        var index = _sessions.FindIndex(s => s.Id == sessionId);
        if (index < 0) return false;
        _sessions.RemoveAt(index);
        if (_activeSessionId == sessionId)
            _activeSessionId = _sessions.Count == 0 ? null : _sessions[Math.Min(index, _sessions.Count - 1)].Id;
        return true;
    }

    public bool Move(string sessionId, int newIndex)
    {
        if (newIndex < 0 || newIndex >= _sessions.Count) return false;
        var previousIndex = _sessions.FindIndex(s => s.Id == sessionId);
        if (previousIndex < 0) return false;
        var value = _sessions[previousIndex];
        _sessions.RemoveAt(previousIndex);
        _sessions.Insert(newIndex, value);
        return true;
    }

    /// <summary>
    /// Returns an ephemeral human preview; never writes to a terminal,
    /// a clipboard, a network socket, the browser, or a DLE record.
    /// Callers must not log the supplied preview text.
    /// </summary>
    public WorkbenchTransferPreview PreviewTransfer(
        string sessionId, string direction, string previewText)
    {
        if (!_sessions.Any(s => s.Id == sessionId))
            throw new ArgumentException("UNKNOWN_SESSION_ID", nameof(sessionId));
        if (direction is not ("chat-to-terminal" or "terminal-to-chat"))
            throw new ArgumentException("INVALID_TRANSFER_DIRECTION", nameof(direction));
        if (string.IsNullOrWhiteSpace(previewText) || previewText.Length > 16_384 ||
            previewText.Contains('\0'))
            throw new ArgumentException("INVALID_TRANSFER_TEXT", nameof(previewText));

        return new("suprachat-workbench-transfer-preview/v1", sessionId, direction, previewText,
            Encoding.UTF8.GetByteCount(previewText),
            RequiresExplicitCommit: true, WasApplied: false, CommandExecutionPermitted: false,
            direction == "chat-to-terminal" ? "INSERT_ONLY_NOT_EXECUTE" : "DRAFT_ONLY_NOT_SUBMIT");
    }

    public static object DescribeContract() => new
    {
        schema = Schema,
        phase = "W0_IN_MEMORY_SEMANTICS_ONLY",
        max_sessions = MaximumSessions,
        paired_terminal_state = "NOT_CONNECTED",
        persistent_state = false,
        chat_submission_supported = false,
        terminal_command_execution_supported = false,
        transfer_commit_supported = false,
        transfer_preview_directions = new[] { "chat-to-terminal", "terminal-to-chat" },
        invariants = new[]
        {
            "One conversation reference and one non-executing terminal slot per session.",
            "Workspace and conversation collisions are visible; no implicit shared mutation.",
            "Transfer previews have no effect and require an explicit later commit path.",
            "No credentials, transcripts or preview text are written to persisted workbench state."
        }
    };

    private static string NormalizeTitle(string? title, int ordinal)
    {
        if (string.IsNullOrWhiteSpace(title)) return $"Session {ordinal}";
        var text = new string(title.Trim().Where(c => !char.IsControl(c)).ToArray());
        if (text.Length is 0 or > 80) throw new ArgumentException("INVALID_SESSION_TITLE", nameof(title));
        return text;
    }

    private static string? NormalizeWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        var value = workspace.Trim().Replace('\\', '/');
        if (value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("INVALID_WORKSPACE_KEY", nameof(workspace));
        return value.TrimEnd('/');
    }
}

public sealed record WorkbenchSession(
    string Id, string Title, string? ConversationUrl, string? WorkspaceKey,
    string TerminalState, bool TerminalExecutionPermitted, bool ChatSubmissionPermitted);

public sealed record WorkbenchCollision(string Type, string LeftSessionId, string RightSessionId);

public sealed record WorkbenchSnapshot(
    string Schema, string? ActiveSessionId,
    IReadOnlyList<WorkbenchSession> Sessions,
    IReadOnlyList<WorkbenchCollision> Collisions,
    int MaxSessions, bool TerminalExecutionPermitted, bool ChatSubmissionPermitted);

public sealed record WorkbenchTransferPreview(
    string Schema, string SessionId, string Direction,
    string PreviewText, int PreviewBytes,
    bool RequiresExplicitCommit, bool WasApplied,
    bool CommandExecutionPermitted, string DestinationMode);
