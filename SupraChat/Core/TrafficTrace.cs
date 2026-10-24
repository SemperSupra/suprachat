using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupraChat.Core;

public enum TraceSurface { ChatGptWebView, ResponsesSse, ResponsesWebSocket, CodexJsonRpc, Synthetic }
public enum TraceAdapter { WebView2, Http, WebSocket, Stdio, Fixture }
public enum TraceOrigin { Unknown, ChatGpt, OpenAiApi, Loopback }
public enum TraceDirection { Ingress, Egress, Local }
public enum TraceEventKind { Request, Response, Chunk, Completed, Cancelled, Reconnected, Error, CaptureGap }
public enum TraceOutcome { Requested, Admitted, Observed, Applied, Verified, Refused, Unknown }
public enum TraceCaptureMode { Off, Metadata, PrivilegedPayload }
public enum TraceOperation { StartMetadata, Stop, Read, ExportMetadata, CapturePayload, CancelProvider, ModifyFirstParty }

/// <summary>Untrusted adapter metadata. Never supply bodies, headers, exception messages or credentials.</summary>
public sealed record TraceObservation(
    TraceSurface ProviderSurface,
    TraceAdapter Adapter,
    TraceDirection Direction,
    TraceEventKind EventKind,
    string? Origin = null,
    string? RequestId = null,
    string? StreamId = null,
    long? SourceSequence = null,
    long ByteCount = 0,
    int? Status = null,
    double? LatencyMilliseconds = null,
    TraceOutcome Outcome = TraceOutcome.Observed);

/// <summary>No raw provider strings, payloads, authenticated principal claims or conversation-ID inference.</summary>
public sealed record TrafficTraceEvent(
    string SchemaVersion,
    Guid EventId,
    Guid HostRunId,
    long GlobalSequence,
    Guid SessionId,
    TraceSurface ProviderSurface,
    TraceAdapter Adapter,
    TraceOrigin ObservedOrigin,
    TraceDirection Direction,
    TraceEventKind EventKind,
    DateTimeOffset WallTimeUtc,
    double MonotonicOffsetMilliseconds,
    string? RequestId,
    string? StreamId,
    long? SourceSequence,
    long ByteCount,
    int? Status,
    double? LatencyMilliseconds,
    TraceOutcome Outcome,
    bool CaptureGap)
{
    public string PayloadClassification => "METADATA_ONLY";
    public string RedactionState => "ALLOWLISTED_PSEUDONYMIZED";
    // Absence of observed gaps cannot prove that a browser/transport supplied every event.
    public string Completeness => "UNKNOWN";
    public string Confidence => "ADAPTER_REPORTED";
}

public sealed record TracePage(
    Guid SessionId,
    IReadOnlyList<TrafficTraceEvent> Events,
    long NextCursor,
    long EvictedThroughSequence,
    long TotalEvictedEvents,
    bool CaptureGap,
    TraceCaptureMode Mode)
{
    public string SchemaVersion => "suprachat-trace/v1";
    public string Completeness => "UNKNOWN";
}

/// <summary>In-process local-operator capability. Not serializable IPC authentication or a W8 principal.</summary>
public sealed class LocalTraceSession
{
    public Guid SessionId { get; }
    internal LocalTraceSession(Guid sessionId) => SessionId = sessionId;
}

public static class TracePolicy
{
    // Payload capture and provider actuation have no implementation or authority in T0.
    public static bool Allows(TraceOperation operation) => operation is
        TraceOperation.StartMetadata or TraceOperation.Stop or TraceOperation.Read or TraceOperation.ExportMetadata;
}

public interface ITraceSource
{
    IAsyncEnumerable<TraceObservation> ObserveAsync(CancellationToken cancellationToken = default);
}

public interface ITraceSink
{
    bool TryObserve(LocalTraceSession session, TraceObservation observation);
}

/// <summary>
/// Future owner-private encrypted store boundary. No implementation, plaintext fallback, automatic
/// capture, or exported payload reference is admitted by this metadata-only tranche.
/// </summary>
public interface IEncryptedTracePayloadVault
{
    ValueTask<Guid> StoreEncryptedAsync(Guid sessionId, ReadOnlyMemory<byte> payload,
        DateTimeOffset expiresUtc, CancellationToken cancellationToken);
    ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Bounded single-process observation store, separate from durable session authority. Calls never
/// invoke subscribers or persist raw data. Each session owns a ring; global order is host-run local.
/// </summary>
public sealed class TraceNormalizer : ITraceSink, IDisposable
{
    private sealed class SessionState(LocalTraceSession capability)
    {
        public LocalTraceSession Capability { get; } = capability;
        public TraceCaptureMode Mode;
        public Queue<TrafficTraceEvent> Events { get; } = new();
        public long EvictedThrough;
        public long EvictedCount;
        public bool PendingGap;
    }

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionState> _sessions = new();
    private readonly byte[] _aliasKey = RandomNumberGenerator.GetBytes(32);
    private readonly Guid _hostRunId = Guid.NewGuid();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly int _capacity;
    private readonly int _maxSessions;
    private long _sequence;
    private bool _disposed;

    public TraceNormalizer(int capacityPerSession = 256, int maxSessions = 16)
    {
        if (capacityPerSession is < 1 or > 4096 || maxSessions is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(capacityPerSession), "Trace bounds exceeded.");
        _capacity = capacityPerSession;
        _maxSessions = maxSessions;
    }

    public LocalTraceSession CreateSession() => CreateSession(Guid.NewGuid());

    /// <summary>Owner-only composition with an existing authoritative session ID. The ID alone grants no access.</summary>
    public LocalTraceSession CreateSession(Guid authoritativeSessionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.Count >= _maxSessions) throw new InvalidOperationException("Trace session limit reached.");
            if (authoritativeSessionId == Guid.Empty || _sessions.ContainsKey(authoritativeSessionId))
                throw new ArgumentException("Invalid or duplicate trace session.");
            var capability = new LocalTraceSession(authoritativeSessionId);
            _sessions.Add(capability.SessionId, new SessionState(capability));
            return capability;
        }
    }

    public void SetCapture(LocalTraceSession session, TraceCaptureMode mode)
    {
        lock (_gate)
        {
            var state = Authorize(session);
            if (mode is not (TraceCaptureMode.Off or TraceCaptureMode.Metadata))
                throw new UnauthorizedAccessException("Trace payload capture is unavailable.");
            // A stopped interval is always disclosed if capture subsequently resumes.
            if (state.Mode == TraceCaptureMode.Metadata && mode == TraceCaptureMode.Off) state.PendingGap = true;
            state.Mode = mode;
        }
    }

    public bool IsCapturing(LocalTraceSession session)
    {
        lock (_gate) return Authorize(session).Mode == TraceCaptureMode.Metadata;
    }

    internal (TraceCaptureMode Mode, int Count) CaptureState(LocalTraceSession session)
    { lock (_gate) { var state = Authorize(session); return (state.Mode, state.Events.Count); } }

    internal string StreamAlias(LocalTraceSession session, string id)
    {
        lock (_gate) { Authorize(session); return Alias(session.SessionId, "stream", id)!; }
    }

    public bool TryObserve(LocalTraceSession session, TraceObservation observation)
    {
        lock (_gate)
        {
            var state = Authorize(session);
            if (state.Mode == TraceCaptureMode.Off) return false;
            ArgumentNullException.ThrowIfNull(observation);
            if (!Valid(observation))
            {
                // Do not echo rejected input in errors/logs. The next retained event discloses loss.
                state.PendingGap = true;
                return false;
            }

            var captureGap = state.PendingGap || observation.EventKind == TraceEventKind.CaptureGap;
            var item = new TrafficTraceEvent("suprachat-trace/v1", Guid.NewGuid(), _hostRunId,
                checked(++_sequence), session.SessionId, observation.ProviderSurface, observation.Adapter,
                NormalizeOrigin(observation.Origin), observation.Direction, observation.EventKind,
                DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                Alias(session.SessionId, "request", observation.RequestId),
                Alias(session.SessionId, "stream", observation.StreamId), observation.SourceSequence,
                observation.ByteCount, observation.Status, observation.LatencyMilliseconds,
                observation.Outcome, captureGap);
            if (state.Events.Count == _capacity)
            {
                state.EvictedThrough = state.Events.Dequeue().GlobalSequence;
                state.EvictedCount++;
            }
            state.Events.Enqueue(item);
            state.PendingGap = false;
            return true;
        }
    }

    public TracePage Read(LocalTraceSession session, long afterSequence = 0, int limit = 100)
    {
        lock (_gate)
        {
            var state = Authorize(session);
            if (afterSequence < 0 || afterSequence > _sequence || limit is < 1 or > 1000)
                throw new ArgumentOutOfRangeException(nameof(afterSequence), "Invalid trace page bounds.");
            var items = state.Events.Where(e => e.GlobalSequence > afterSequence).Take(limit).ToArray();
            return new TracePage(session.SessionId, Array.AsReadOnly(items),
                items.Length == 0 ? afterSequence : items[^1].GlobalSequence,
                state.EvictedThrough, state.EvictedCount,
                state.PendingGap || afterSequence < state.EvictedThrough || items.Any(e => e.CaptureGap), state.Mode);
        }
    }

    public void CloseSession(LocalTraceSession session)
    {
        lock (_gate)
        {
            Authorize(session);
            _sessions.Remove(session.SessionId);
        }
    }

    /// <summary>Adapter failure is a gap, never a claim of complete capture. Error text stays out.</summary>
    public async Task PumpAsync(LocalTraceSession session, ITraceSource source, CancellationToken cancellationToken = default)
    {
        lock (_gate) Authorize(session);
        try
        {
            await foreach (var observation in source.ObserveAsync(cancellationToken).WithCancellation(cancellationToken))
                TryObserve(session, observation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkGap(session);
            throw new OperationCanceledException("Trace source cancelled; capture is incomplete.", cancellationToken);
        }
        catch
        {
            MarkGap(session);
            throw new InvalidOperationException("Trace source failed; capture is incomplete.");
        }
    }

    private void MarkGap(LocalTraceSession session)
    {
        lock (_gate)
            if (!_disposed && _sessions.TryGetValue(session.SessionId, out var state) && ReferenceEquals(state.Capability, session))
                state.PendingGap = true;
    }

    private SessionState Authorize(LocalTraceSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (session is null || !_sessions.TryGetValue(session.SessionId, out var state) || !ReferenceEquals(state.Capability, session))
            throw new UnauthorizedAccessException("Trace session capability refused.");
        return state;
    }

    private string? Alias(Guid sessionId, string domain, string? value) => value is null ? null :
        Convert.ToHexString(HMACSHA256.HashData(_aliasKey, Encoding.UTF8.GetBytes($"{sessionId:N}:{domain}:{value}"))).ToLowerInvariant();

    private static bool Valid(TraceObservation o) =>
        Enum.IsDefined(o.ProviderSurface) && Enum.IsDefined(o.Adapter) && Enum.IsDefined(o.Direction) && Enum.IsDefined(o.EventKind) && Enum.IsDefined(o.Outcome) &&
        o.ByteCount >= 0 && o.SourceSequence is not < 0 && o.Status is not (< 100 or > 599) &&
        (o.LatencyMilliseconds is null || (double.IsFinite(o.LatencyMilliseconds.Value) && o.LatencyMilliseconds >= 0)) &&
        (o.Origin?.Length ?? 0) <= 4096 && (o.RequestId?.Length ?? 0) <= 512 && (o.StreamId?.Length ?? 0) <= 512;

    private static TraceOrigin NormalizeOrigin(string? origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0) return TraceOrigin.Unknown;
        if (uri.Scheme == "https" && uri.IsDefaultPort)
        {
            if (uri.Host == "chatgpt.com") return TraceOrigin.ChatGpt;
            if (uri.Host == "api.openai.com") return TraceOrigin.OpenAiApi;
        }
        return uri.IsLoopback && uri.Scheme is "http" or "https" ? TraceOrigin.Loopback : TraceOrigin.Unknown;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _sessions.Clear();
            CryptographicOperations.ZeroMemory(_aliasKey);
        }
    }
}
