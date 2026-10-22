using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// W5 pure in-process concurrency core for multiple audience identities and
/// simultaneous sessions. All mutation outcomes (accepted OR rejected) are
/// idempotency-cached and returned on retry. No network, filesystem, browser,
/// terminal execution, credentials or transcript content is accessed.
/// An external authenticated host and durable replay journal are NOT provided.
/// </summary>
public sealed class SharedSessionLedger
{
    public const string Schema = "suprachat-shared-session-ledger/v1";
    public const int MaxSessions = 32;
    public const int MaxEvents = 1024;
    public const int MaxIdempotencyRecords = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionLedgerState> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PriorAction> _operations = new(StringComparer.Ordinal);
    private readonly List<SessionLedgerEvent> _events = new();
    private long _sequence;

    /// <summary>
    /// Returns first outcome for the same idempotency key + same operation
    /// fingerprint. Replayed outcomes are historical, NOT fresh snapshots.
    /// Keys are global across all audience labels to prevent cross-actor reuse.
    /// </summary>
    public SessionLedgerResult Open(string idempotencyKey, string audience,
        string title, string? conversationUrl = null, string? workspaceKey = null)
    {
        ValidateIdempotencyKey(idempotencyKey);
        ValidateAudience(audience);
        ValidateTitle(title);
        var normalizedUrl = NormalizeConversation(conversationUrl);
        var workspace = NormalizeWorkspace(workspaceKey);
        var fingerprint = Fingerprint("open", audience, null, 0, title, normalizedUrl, workspace);
        lock (_gate)
        {
            if (_operations.TryGetValue(idempotencyKey, out var prior))
                return Replay(prior, fingerprint);
            if (_operations.Count >= MaxIdempotencyRecords)
                return new(Schema, false, false, "IDEMPOTENCY_CAPACITY_REACHED", null, _sequence);
            if (_sessions.Count >= MaxSessions)
                return Cache(idempotencyKey, fingerprint,
                    new(Schema, false, false, "SESSION_CAPACITY_REACHED", null, _sequence));

            var id = Guid.NewGuid().ToString("N");
            var session = new SessionLedgerState(id, title.Trim(), normalizedUrl,
                workspace, 0, "NO_REMOTE_BINDING", false, false);
            _sessions.Add(id, session);
            AppendEvent(id, audience, "open", 0);
            return Cache(idempotencyKey, fingerprint,
                new(Schema, true, false, "APPLIED", session, _sequence));
        }
    }

    /// <summary>
    /// Atomic compare-and-swap mutation, independent of operator audience.
    /// Allowed operations update reference metadata only, not prompt bodies.
    /// The first failure for a key is also immutable across retries.
    /// </summary>
    public SessionLedgerResult Change(string idempotencyKey, string audience,
        string sessionId, long expectedVersion, string operation, string? value)
    {
        ValidateIdempotencyKey(idempotencyKey);
        ValidateAudience(audience);
        if (!Guid.TryParseExact(sessionId, "N", out _))
            throw new ArgumentException("INVALID_SESSION_ID", nameof(sessionId));
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (operation is not ("set-title" or "set-conversation" or "set-workspace"))
            throw new ArgumentException("UNSUPPORTED_LEDGER_OPERATION", nameof(operation));

        var normalized = operation switch
        {
            "set-title" => NormalizedTitle(value),
            "set-conversation" => NormalizeConversation(value),
            "set-workspace" => NormalizeWorkspace(value),
            _ => null
        };
        var fingerprint = Fingerprint(operation, audience, sessionId, expectedVersion, normalized);
        lock (_gate)
        {
            if (_operations.TryGetValue(idempotencyKey, out var previous))
                return Replay(previous, fingerprint);
            if (_operations.Count >= MaxIdempotencyRecords)
                return new(Schema, false, false, "IDEMPOTENCY_CAPACITY_REACHED", null, _sequence);
            if (!_sessions.TryGetValue(sessionId, out var session))
                return Cache(idempotencyKey, fingerprint,
                    new(Schema, false, false, "SESSION_NOT_FOUND", null, _sequence));
            if (session.Version != expectedVersion)
                return Cache(idempotencyKey, fingerprint,
                    new(Schema, false, false, "VERSION_CONFLICT", session, _sequence));

            SessionLedgerState next = operation switch
            {
                "set-title" => session with {Title = normalized!, Version = session.Version + 1},
                "set-conversation" => session with {ConversationUrl = normalized, Version = session.Version + 1},
                "set-workspace" => session with {WorkspaceKey = normalized, Version = session.Version + 1},
                _ => throw new InvalidOperationException("UNREACHABLE_OPERATION")
            };
            _sessions[sessionId] = next;
            AppendEvent(sessionId, audience, operation, next.Version);
            return Cache(idempotencyKey, fingerprint,
                new(Schema, true, false, "APPLIED", next, _sequence));
        }
    }

    public SessionLedgerState? Inspect(string sessionId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(sessionId);
    }

    public SessionLedgerInventory InspectAll()
    {
        lock (_gate)
            return new(Schema, _sequence,
                _sessions.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(),
                _events.ToArray(),
                DurableAcrossProcessRestart: false,
                AuthenticatedAudiences: false,
                ChatSubmissionAllowed: false,
                TerminalExecutionAllowed: false,
                CrossProcessTransportAvailable: false);
    }

    public static object DescribeContract() => new
    {
        schema = Schema,
        phase = "W5_IN_PROCESS_CONCURRENT_CAS_CORE",
        max_sessions = MaxSessions,
        max_idempotency_outcomes = MaxIdempotencyRecords,
        max_metadata_events = MaxEvents,
        atomic_same_process_mutation = true,
        exact_same_key_same_payload_replay = true,
        changed_payload_on_key_reuse_refused = true,
        cached_rejection_replay = true,
        optimistic_version_compare_and_swap = true,
        cross_process_transport_available = false,
        persistent_recovery_available = false,
        authenticated_audience_identity = false,
        provider_prompt_submit_available = false,
        terminal_command_execution_available = false,
        note = "Metadata-only in-process reference core. Durable DLE, local authenticated API and UI/agent binding still need separate qualification."
    };

    private SessionLedgerResult Cache(string key, string fingerprint, SessionLedgerResult value)
    {
        _operations.Add(key, new(fingerprint, value));
        return value;
    }

    private static SessionLedgerResult Replay(PriorAction prior, string fingerprint) =>
        prior.Fingerprint == fingerprint
            ? prior.Outcome with {IsReplay = true}
            : new(Schema, false, false, "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT", null,
                prior.Outcome.Sequence);

    private void AppendEvent(string id, string audience, string kind, long version)
    {
        _events.Add(new(++_sequence, id, audience, kind, version));
        if (_events.Count > MaxEvents) _events.RemoveAt(0);
    }

    private static string Fingerprint(string operation, string audience,
        string? sessionId, long version, params string?[] data)
    {
        var canonical = JsonSerializer.Serialize(new {operation, audience, sessionId, version, data});
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length is < 12 or > 128 ||
            key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not ':' and not '.'))
            throw new ArgumentException("INVALID_IDEMPOTENCY_KEY");
    }
    private static void ValidateAudience(string audience)
    {
        if (audience is not ("human" or "automation" or "agent"))
            throw new ArgumentException("INVALID_AUDIENCE_LABEL");
    }
    private static void ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 80 ||
            title.Any(char.IsControl))
            throw new ArgumentException("INVALID_SESSION_TITLE");
    }
    private static string NormalizedTitle(string? title)
    {
        ValidateTitle(title);
        return title!.Trim();
    }
    private static string? NormalizeConversation(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var inspected = ChatGptControlContract.Inspect(url);
        if (!inspected.Recognized || inspected.CanonicalUrl is null)
            throw new ArgumentException("INVALID_CONVERSATION_REFERENCE");
        return inspected.CanonicalUrl;
    }
    private static string? NormalizeWorkspace(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = key.Trim().Replace('\\', '/').TrimEnd('/');
        if (normalized.Length is 0 or > 512 || normalized.Any(char.IsControl))
            throw new ArgumentException("INVALID_WORKSPACE_REFERENCE");
        return normalized;
    }

    private sealed record PriorAction(string Fingerprint, SessionLedgerResult Outcome);
}

public sealed record SessionLedgerState(
    string Id, string Title, string? ConversationUrl, string? WorkspaceKey,
    long Version, string RemoteBindingState, bool ChatSubmissionAllowed,
    bool TerminalExecutionAllowed);

public sealed record SessionLedgerEvent(
    long Sequence, string SessionId, string Audience, string Operation, long SessionVersion);

public sealed record SessionLedgerResult(
    string Schema, bool Applied, bool IsReplay, string Status,
    SessionLedgerState? Session, long Sequence);

public sealed record SessionLedgerInventory(
    string Schema, long Sequence,
    IReadOnlyList<SessionLedgerState> Sessions,
    IReadOnlyList<SessionLedgerEvent> Events,
    bool DurableAcrossProcessRestart, bool AuthenticatedAudiences,
    bool ChatSubmissionAllowed, bool TerminalExecutionAllowed,
    bool CrossProcessTransportAvailable);
