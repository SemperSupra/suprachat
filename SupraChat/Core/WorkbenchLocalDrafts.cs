namespace SupraChat.Core;

/// <summary>
/// Two-phase, one-shot transfer into a session-specific LOCAL TEXT BUFFER only.
/// This component has no terminal handle, browser, clipboard, filesystem or
/// credential access, and exposes no execution or chat-submission capability.
/// </summary>
public sealed class WorkbenchLocalDrafts : IDisposable
{
    public const string Schema = "suprachat-local-draft-transfer/v1";
    private static readonly TimeSpan MaximumApprovalAge = TimeSpan.FromMinutes(2);
    private readonly SessionWorkbench _workbench;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, PendingReview> _reviews = new();
    private readonly Dictionary<string, WorkbenchLocalDraftPair> _drafts = new();
    private bool _disposed;

    public WorkbenchLocalDrafts(SessionWorkbench workbench, Func<DateTimeOffset>? now = null)
    {
        _workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _workbench.ActiveSessionChanged += OnActiveSessionChanged;
        _workbench.SessionClosed += OnSessionClosed;
    }

    internal int PendingReviewCount => _reviews.Count;
    internal int RetainedSessionCount => _drafts.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _workbench.ActiveSessionChanged -= OnActiveSessionChanged;
        _workbench.SessionClosed -= OnSessionClosed;
        _reviews.Clear();
        _drafts.Clear();
        _disposed = true;
    }

    public WorkbenchLocalDraftReview Prepare(string sessionId, string direction, string input)
    {
        ThrowIfDisposed();
        var preview = _workbench.PreviewTransfer(sessionId, direction, input);
        EnsureSelected(sessionId);
        var reviewId = Guid.NewGuid().ToString("N");
        _reviews.Remove(sessionId);
        _reviews[sessionId] = new(reviewId, preview.Direction, preview.PreviewText, _now());
        return new(Schema, reviewId, sessionId, direction, preview.PreviewText,
            preview.PreviewBytes, ExpiresAfterSeconds: (int)MaximumApprovalAge.TotalSeconds,
            RequiresExplicitUserAction: true, WasApplied: false,
            ChatSubmissionPermitted: false, TerminalExecutionPermitted: false);
    }

    public WorkbenchLocalDraftCommit StageReviewed(string sessionId, string reviewId, string stillVisibleInput)
    {
        ThrowIfDisposed();
        EnsureSelected(sessionId);
        if (!_reviews.TryGetValue(sessionId, out var pending) ||
            pending.Id != reviewId)
            throw new InvalidOperationException("STALE_OR_CHANGED_TRANSFER_REVIEW");
        if (!string.Equals(pending.Content, stillVisibleInput, StringComparison.Ordinal))
        {
            _reviews.Remove(sessionId); // an edited review cannot be restored and reused
            throw new InvalidOperationException("STALE_OR_CHANGED_TRANSFER_REVIEW");
        }
        // Expired reviews must never be silently accepted.
        if (_now() < pending.CreatedAt || _now() - pending.CreatedAt > MaximumApprovalAge)
        {
            _reviews.Remove(sessionId);
            throw new InvalidOperationException("TRANSFER_REVIEW_EXPIRED");
        }
        _reviews.Remove(sessionId); // consume authority before changing buffer
        var old = Read(sessionId);
        var updated = pending.Direction == "chat-to-terminal"
            ? old with { TerminalInputDraft = pending.Content }
            : old with { ChatInputDraft = pending.Content };
        _drafts[sessionId] = updated;
        return new(Schema, sessionId, pending.Direction,
            pending.Direction == "chat-to-terminal" ? "LOCAL_TERMINAL_INPUT_BUFFER" : "LOCAL_CHAT_INPUT_BUFFER",
            DraftStaged: true, ChatSubmitted: false, CommandExecuted: false, RemoteRequestCreated: false);
    }

    public WorkbenchLocalDraftPair Read(string sessionId)
    {
        ThrowIfDisposed();
        if (!_workbench.Snapshot().Sessions.Any(s => s.Id == sessionId))
            throw new ArgumentException("UNKNOWN_SESSION_ID", nameof(sessionId));
        return _drafts.TryGetValue(sessionId, out var drafts)
            ? drafts : new(string.Empty, string.Empty);
    }

    public void DiscardPending() => _reviews.Clear();

    public void ForgetSession(string sessionId)
    {
        _reviews.Remove(sessionId);
        _drafts.Remove(sessionId);
    }

    private void OnActiveSessionChanged() => _reviews.Clear();

    private void OnSessionClosed(string sessionId) => ForgetSession(sessionId);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnsureSelected(string sessionId)
    {
        if (!string.Equals(_workbench.Snapshot().ActiveSessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("SESSION_NOT_SELECTED");
    }

    private sealed record PendingReview(string Id, string Direction, string Content, DateTimeOffset CreatedAt);
}

public sealed record WorkbenchLocalDraftPair(string TerminalInputDraft, string ChatInputDraft);

public sealed record WorkbenchLocalDraftReview(
    string Schema, string ReviewId, string SessionId, string Direction,
    string PreviewText, int PreviewBytes, int ExpiresAfterSeconds,
    bool RequiresExplicitUserAction, bool WasApplied,
    bool ChatSubmissionPermitted, bool TerminalExecutionPermitted);

public sealed record WorkbenchLocalDraftCommit(
    string Schema, string SessionId, string Direction, string Destination,
    bool DraftStaged, bool ChatSubmitted, bool CommandExecuted, bool RemoteRequestCreated);
