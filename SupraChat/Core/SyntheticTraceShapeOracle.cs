using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

public enum TraceIngestOutcome
{
    Off,
    Observed,
    Ignored,
    Gap
}

public sealed class TraceShapeSessionHandle
{
    internal Guid Id { get; } = Guid.NewGuid();
}

public sealed record TraceShapeEvent(
    string Kind,
    string? Correlation,
    long Bytes,
    int? Status,
    string Surface = "SYNTHETIC");

public sealed record TraceShapeSnapshot(
    string Schema,
    string Grade,
    string Completeness,
    bool Enabled,
    long Gaps,
    IReadOnlyList<TraceShapeEvent> Events);

/// <summary>
/// Candidate synthetic-only shape oracle (Package P4 / Milestone M2).
/// Redacts raw URLs, headers, payloads, cookies, and tokens.
/// Produces HMAC correlation aliases and tracks capture-gaps within strict bounded memory limits.
/// </summary>
public sealed class SyntheticTraceShapeOracle
{
    public const string Schema = "suprachat-trace-shape-candidate/v1";
    public const string Grade = "SYNTHETIC_ONLY";
    public const string Completeness = "UNKNOWN";

    private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
    {
        "requestWillBeSent",
        "responseReceived",
        "dataReceived",
        "loadingFinished",
        "loadingFailed",
        "webSocketCreated",
        "webSocketFrameSent",
        "webSocketFrameReceived",
        "webSocketClosed",
        "eventSourceMessageReceived"
    };

    private sealed class SessionState
    {
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
        public Dictionary<string, string> Active { get; } = new(StringComparer.Ordinal);
        public List<TraceShapeEvent> Events { get; } = new();
        public long Gaps { get; set; }
        public bool Enabled { get; set; }
    }

    private readonly object _gate = new();
    private readonly Uri _origin;
    private readonly int _limit;
    private readonly Dictionary<TraceShapeSessionHandle, SessionState> _sessions = new();

    public Uri Origin => _origin;
    public int Limit => _limit;

    public SyntheticTraceShapeOracle(string origin = "https://chatgpt.com", int limit = 32)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin))
            throw new ArgumentException("Invalid origin URI.", nameof(origin));
        if (limit is < 2 or > 128)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 2 and 128.");

        _origin = parsedOrigin;
        _limit = limit;
    }

    public TraceShapeSessionHandle Session()
    {
        lock (_gate)
        {
            if (_sessions.Count >= 4)
                throw new InvalidOperationException("Trace shape session capacity reached (max 4).");

            var handle = new TraceShapeSessionHandle();
            _sessions[handle] = new SessionState();
            return handle;
        }
    }

    public void Enable(TraceShapeSessionHandle handle, bool yes)
    {
        lock (_gate)
        {
            var state = GetState(handle);
            state.Enabled = yes;
            if (!yes)
            {
                state.Active.Clear();
                state.Gaps++;
            }
        }
    }

    public void Revoke(TraceShapeSessionHandle handle)
    {
        lock (_gate)
        {
            var state = GetState(handle);
            state.Enabled = false;
            state.Active.Clear();
            _sessions.Remove(handle);
        }
    }

    public void Close(TraceShapeSessionHandle handle) => Revoke(handle);

    public TraceIngestOutcome Ingest(TraceShapeSessionHandle handle, string name, string? raw)
    {
        lock (_gate)
        {
            var state = GetState(handle);
            if (!state.Enabled)
                return TraceIngestOutcome.Off;

            if (string.IsNullOrEmpty(raw) ||
                Encoding.UTF8.GetByteCount(raw) > 65536 ||
                !AllowedEvents.Contains(name))
            {
                EmitGap(state);
                return TraceIngestOutcome.Gap;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch
            {
                EmitGap(state);
                return TraceIngestOutcome.Gap;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("requestId", out var reqIdElem) ||
                    reqIdElem.ValueKind != JsonValueKind.String)
                {
                    EmitGap(state);
                    return TraceIngestOutcome.Gap;
                }

                var requestId = reqIdElem.GetString();
                if (string.IsNullOrEmpty(requestId) || requestId.Length < 1 || requestId.Length > 128)
                {
                    EmitGap(state);
                    return TraceIngestOutcome.Gap;
                }

                if (name is "requestWillBeSent" or "webSocketCreated")
                {
                    string? url = null;
                    if (name == "requestWillBeSent" &&
                        root.TryGetProperty("request", out var reqElem) &&
                        reqElem.TryGetProperty("url", out var urlElem) &&
                        urlElem.ValueKind == JsonValueKind.String)
                    {
                        url = urlElem.GetString();
                    }
                    else if (name == "webSocketCreated" &&
                             root.TryGetProperty("url", out var wsUrlElem) &&
                             wsUrlElem.ValueKind == JsonValueKind.String)
                    {
                        url = wsUrlElem.GetString();
                    }

                    if (!SameOrigin(url, _origin))
                    {
                        state.Active.Remove(requestId);
                        return TraceIngestOutcome.Ignored;
                    }

                    if (state.Active.Count >= 32 && !state.Active.ContainsKey(requestId))
                    {
                        EmitGap(state);
                        return TraceIngestOutcome.Gap;
                    }

                    var aliasBytes = HMACSHA256.HashData(state.Key, Encoding.UTF8.GetBytes(requestId));
                    var alias = Convert.ToHexStringLower(aliasBytes)[..20];
                    state.Active[requestId] = alias;
                }

                if (!state.Active.TryGetValue(requestId, out var correlationAlias))
                    return TraceIngestOutcome.Ignored;

                if (name == "responseReceived")
                {
                    string? respUrl = null;
                    if (root.TryGetProperty("response", out var respElem) &&
                        respElem.TryGetProperty("url", out var respUrlElem) &&
                        respUrlElem.ValueKind == JsonValueKind.String)
                    {
                        respUrl = respUrlElem.GetString();
                    }

                    if (!SameOrigin(respUrl, _origin))
                    {
                        state.Active.Remove(requestId);
                        EmitGap(state);
                        return TraceIngestOutcome.Gap;
                    }
                }

                long bytes = 0;
                int? status = null;

                if (name == "dataReceived")
                {
                    if (!root.TryGetProperty("dataLength", out var lenElem) ||
                        lenElem.ValueKind != JsonValueKind.Number ||
                        !lenElem.TryGetInt64(out var len) ||
                        len < 0)
                    {
                        EmitGap(state);
                        return TraceIngestOutcome.Gap;
                    }
                    bytes = len;
                }

                if (name == "responseReceived" &&
                    root.TryGetProperty("response", out var responseObj) &&
                    responseObj.TryGetProperty("status", out var statusElem) &&
                    statusElem.ValueKind == JsonValueKind.Number &&
                    statusElem.TryGetInt32(out var statusCode) &&
                    statusCode is >= 100 and <= 599)
                {
                    status = statusCode;
                }

                EmitEvent(state, new TraceShapeEvent(name, correlationAlias, bytes, status, "SYNTHETIC"));

                if (name is "loadingFinished" or "loadingFailed" or "webSocketClosed")
                {
                    state.Active.Remove(requestId);
                }

                return TraceIngestOutcome.Observed;
            }
        }
    }

    public TraceShapeSnapshot Snapshot(TraceShapeSessionHandle handle)
    {
        lock (_gate)
        {
            var state = GetState(handle);
            return new TraceShapeSnapshot(
                Schema,
                Grade,
                Completeness,
                state.Enabled,
                state.Gaps,
                state.Events.ToList());
        }
    }

    private SessionState GetState(TraceShapeSessionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_sessions.TryGetValue(handle, out var state))
            throw new ArgumentException("Unknown, revoked, or uninitialized trace capability handle.", nameof(handle));
        return state;
    }

    private void EmitGap(SessionState state)
    {
        state.Gaps++;
        EmitEvent(state, new TraceShapeEvent("CAPTURE_GAP", null, 0, null, "SYNTHETIC"));
    }

    private void EmitEvent(SessionState state, TraceShapeEvent evt)
    {
        if (state.Events.Count >= _limit)
        {
            state.Events.RemoveAt(0);
            state.Gaps++;
        }
        state.Events.Add(evt);
    }

    public static bool SameOrigin(string? candidateUrl, Uri targetOrigin)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl))
            return false;
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri))
            return false;

        // Protocol matching: allow https/wss or http/ws
        var targetScheme = targetOrigin.Scheme;
        var validScheme = (targetScheme == "https" && (uri.Scheme == "https" || uri.Scheme == "wss")) ||
                          (targetScheme == "http" && (uri.Scheme == "http" || uri.Scheme == "ws"));

        if (!validScheme)
            return false;

        if (!string.Equals(uri.Host, targetOrigin.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        if (uri.Port != targetOrigin.Port)
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        return true;
    }
}
