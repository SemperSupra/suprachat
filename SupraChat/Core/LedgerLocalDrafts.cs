using System.Text;

namespace SupraChat.Core;

/// <summary>
/// Two-phase, UI-local text staging bound to the accepted W5 ledger's session IDs.
/// Does not create sessions, change session metadata, persist files, inspect a browser,
/// launch a terminal, send a ChatGPT turn, or expose a command execution API.
/// The optional text is user supplied; never put it in receipts or diagnostics.
/// </summary>
public sealed class LedgerLocalDrafts : IDisposable
{
    public const string Schema = "suprachat-ledger-local-draft/v1";
    public const int MaximumInputBytes = 16 * 1024;
    public static readonly TimeSpan MaximumReviewAge = TimeSpan.FromMinutes(2);

    private readonly SharedSessionLedger _ledger;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, LedgerDraftBuffers> _drafts = new(StringComparer.Ordinal);
    private string? _selectedId;
    private PendingReview? _pending;
    private bool _disposed;

    public LedgerLocalDrafts(SharedSessionLedger ledger, Func<DateTimeOffset>? now = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string? SelectedId => _selectedId;

    public void Select(string? sessionId)
    {
        ThrowIfDisposed();
        if (sessionId is not null && _ledger.Inspect(sessionId) is null)
        {
            _pending = null;
            _selectedId = null;
            throw new ArgumentException("UNKNOWN_LEDGER_SESSION", nameof(sessionId));
        }
        if (sessionId == _selectedId) return;
        _pending = null; // selection change invalidates approval, not existing drafts
        _selectedId = sessionId;
    }

    public LedgerDraftReview Prepare(string direction, string input)
    {
        ThrowIfDisposed();
        var state = RequireSelected();
        if (direction is not ("chat-to-terminal" or "terminal-to-chat"))
            throw new ArgumentException("INVALID_TRANSFER_DIRECTION", nameof(direction));
        if (string.IsNullOrWhiteSpace(input) || input.Contains('\0') ||
            Encoding.UTF8.GetByteCount(input) > MaximumInputBytes)
            throw new ArgumentException("INVALID_TRANSFER_TEXT", nameof(input));
        var now = _now();
        _pending = new(Guid.NewGuid().ToString("N"), state.Id, state.Version,
            direction, input, now);
        return new(Schema, _pending.ReviewId, state.Id, direction, input,
            Encoding.UTF8.GetByteCount(input), (int)MaximumReviewAge.TotalSeconds,
            Applied: false, ChatSubmitted: false, CommandExecuted: false);
    }

    public LedgerDraftStageReceipt StageReviewed(string reviewId, string stillVisibleText)
    {
        ThrowIfDisposed();
        // A review token may be exercised exactly once; consume before doing work.
        var pending = _pending;
        _pending = null;
        if (pending is null || pending.ReviewId != reviewId ||
            !string.Equals(_selectedId, pending.SessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("STALE_OR_CHANGED_TRANSFER_REVIEW");
        var current = _ledger.Inspect(pending.SessionId);
        if (current is null || current.Version != pending.LedgerVersion)
            throw new InvalidOperationException("LEDGER_SESSION_CHANGED");
        var at = _now();
        if (at < pending.CreatedAt || at - pending.CreatedAt > MaximumReviewAge)
            throw new InvalidOperationException("TRANSFER_REVIEW_EXPIRED");
        if (!string.Equals(pending.Text, stillVisibleText, StringComparison.Ordinal))
            throw new InvalidOperationException("STALE_OR_CHANGED_TRANSFER_REVIEW");
        if (!_drafts.TryGetValue(pending.SessionId, out var old))
            old = new(string.Empty, string.Empty);
        _drafts[pending.SessionId] = pending.Direction == "chat-to-terminal"
            ? old with { TerminalInput = pending.Text }
            : old with { ChatInput = pending.Text };
        return new(Schema, pending.SessionId, pending.Direction,
            "LOCAL_DRAFT_ONLY", Staged: true, ChatSubmitted: false,
            CommandExecuted: false, RemoteRequestCreated: false);
    }

    public LedgerDraftBuffers ReadSelected()
    {
        ThrowIfDisposed();
        var current = RequireSelected();
        return _drafts.TryGetValue(current.Id, out var buffers)
            ? buffers : new(string.Empty, string.Empty);
    }

    public void DiscardPending() => _pending = null;

    public void Forget(string sessionId)
    {
        ThrowIfDisposed();
        if (_selectedId == sessionId) Select(null);
        _drafts.Remove(sessionId);
        if (_pending?.SessionId == sessionId) _pending = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _pending = null;
        _selectedId = null;
        _drafts.Clear();
        _disposed = true;
    }

    private SessionLedgerState RequireSelected()
    {
        if (_selectedId is null || _ledger.Inspect(_selectedId) is not { } state)
            throw new InvalidOperationException("SESSION_SELECTION_REQUIRED");
        return state;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record PendingReview(string ReviewId, string SessionId, long LedgerVersion,
        string Direction, string Text, DateTimeOffset CreatedAt);
}

public sealed record LedgerDraftBuffers(string TerminalInput, string ChatInput);

/// <summary>Human-only review descriptor; never log PreviewText or serialize it to CI.</summary>
public sealed record LedgerDraftReview(
    string Schema, string ReviewId, string SessionId, string Direction,
    string PreviewText, int PreviewBytes, int ExpiresAfterSeconds,
    bool Applied, bool ChatSubmitted, bool CommandExecuted);

/// <summary>Safe structured receipt contains no draft text.</summary>
public sealed record LedgerDraftStageReceipt(
    string Schema, string SessionId, string Direction, string Destination,
    bool Staged, bool ChatSubmitted, bool CommandExecuted, bool RemoteRequestCreated);
