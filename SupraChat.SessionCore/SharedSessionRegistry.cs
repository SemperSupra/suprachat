using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.SessionCore;

/// <summary>
/// Public-safe local RDTE. Coordinates synthetic session metadata only.
/// It deliberately has NO provider, browser, terminal, credential or network actuator.
/// Every mutating request is serialized, staged and committed before acknowledging.
/// </summary>
public sealed class SharedSessionRegistry
{
    public const string Schema = "suprachat-shared-sessions-rdte/v1";
    public const int MaxSessions = 16;
    public const int MaxParticipants = 16;
    public const int MaxEventsPerSession = 256;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly object _gate = new();
    private readonly string _storePath;
    private RegistryState _state;

    public SharedSessionRegistry(string storePath)
    {
        _storePath = Path.GetFullPath(storePath);
        _state = File.Exists(_storePath)
            ? JsonSerializer.Deserialize<RegistryState>(File.ReadAllText(_storePath), Json)
                ?? throw new InvalidDataException("INVALID_SESSION_STORE")
            : new RegistryState();
        if (_state.Sessions is null || _state.Creations is null)
            throw new InvalidDataException("INVALID_SESSION_STORE");
    }

    public SessionReply Execute(SessionRequest request)
    {
        lock (_gate)
        {
            try
            {
                if (request is null) return Fail("INVALID_REQUEST");
                var mutates = request.Action is "session/create" or "session/join" or "session/append";
                var staged = mutates ? Clone(_state) : _state;
                var reply = Dispatch(staged, request, out var changed);
                if (reply.Ok && changed)
                {
                    Save(staged);  // Never acknowledge an uncommitted mutation.
                    _state = staged;
                }
                return reply;
            }
            catch (ArgumentException ex) { return Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Fail(ex.Message); }
            // A failed commit must stop the host: an ACK cannot certify durable state.
            // Deliberately do not disguise I/O errors as successful operations.
        }
    }

    private static RegistryState Clone(RegistryState state) =>
        JsonSerializer.Deserialize<RegistryState>(JsonSerializer.Serialize(state, Json), Json)
            ?? throw new InvalidDataException("INVALID_SESSION_STORE");

    private void Save(RegistryState state)
    {
        var directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);
        var temp = _storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static SessionReply Dispatch(RegistryState state, SessionRequest request, out bool changed)
    {
        changed = false;
        switch (request.Action)
        {
            case "capabilities/read":
                return Ok(new {
                    schema = Schema, phase = "SYNTHETIC_LOCAL_RDTE",
                    supported = new[] { "session/create", "session/list", "session/read",
                        "session/join", "session/append", "session/events", "capabilities/read" },
                    provider_chat_submit = false, terminal_execution = false,
                    authenticated_actor_authority = false, server_push = false,
                    remote_transport = false
                });
            case "session/list":
                return Ok(state.Sessions.Values
                    .OrderBy(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new { s.Id, s.Title, s.Revision, s.Quarantined })
                    .ToArray());
            case "session/create":
            {
                var idempotency = Required(request.OperationId, "INVALID_OPERATION_ID", 80);
                var actor = Required(request.ActorId, "INVALID_ACTOR", 80);
                var audience = Audience(request.Audience);
                var title = Required(request.Title, "INVALID_TITLE", 100);
                var digest = Digest(title + "\u001f" + actor + "\u001f" + audience);
                if (state.Creations.TryGetValue(idempotency, out var prior))
                    return prior.Digest == digest
                        ? Ok(state.Sessions[prior.SessionId], replayed: true)
                        : Fail("IDEMPOTENCY_CONFLICT");
                if (state.Sessions.Count >= MaxSessions) return Fail("MAX_SESSIONS_REACHED");
                var session = new SessionRecord {
                    Id = Guid.NewGuid().ToString("N"), Title = title, Revision = 1
                };
                session.Participants.Add(actor, audience);
                session.Events.Add(new SessionEvent(1, "SESSION_CREATED", actor, audience, idempotency,
                    "SYNTHETIC_METADATA", "OBSERVED_LOCAL_ONLY"));
                state.Sessions.Add(session.Id, session);
                state.Creations.Add(idempotency, new CreationRecord(session.Id, digest));
                changed = true;
                return Ok(session);
            }
            case "session/read":
                return Ok(Get(state, request.SessionId));
            case "session/events":
            {
                var session = Get(state, request.SessionId);
                var after = request.AfterRevision ?? 0;
                if (after < 0) return Fail("INVALID_EVENT_CURSOR");
                return Ok(new { session.Id, session.Revision,
                    events = session.Events.Where(e => e.Revision > after).ToArray() });
            }
            case "session/join":
            {
                var session = Get(state, request.SessionId);
                var actor = Required(request.ActorId, "INVALID_ACTOR", 80);
                var audience = Audience(request.Audience);
                if (session.Participants.TryGetValue(actor, out var existing))
                    return existing == audience ? Ok(session, replayed: true) : Fail("PARTICIPANT_CONFLICT");
                if (session.Participants.Count >= MaxParticipants) return Fail("MAX_PARTICIPANTS_REACHED");
                if (session.Events.Count >= MaxEventsPerSession) return Fail("SESSION_EVENT_CAPACITY");
                session.Participants.Add(actor, audience);
                session.Revision++;
                session.Events.Add(new SessionEvent(session.Revision, "PARTICIPANT_JOINED",
                    actor, audience, null, "SYNTHETIC_METADATA", "OBSERVED_LOCAL_ONLY"));
                changed = true;
                return Ok(session);
            }
            case "session/append":
            {
                var session = Get(state, request.SessionId);
                var actor = Required(request.ActorId, "INVALID_ACTOR", 80);
                var op = Required(request.OperationId, "INVALID_OPERATION_ID", 80);
                var kind = request.Kind;
                if (kind is not ("NOTE" or "UNCERTAIN_INTENT_SIMULATION"))
                    return Fail("UNSUPPORTED_EVENT_KIND");
                var detail = Required(request.Detail, "INVALID_DETAIL", 120);
                if (!session.Participants.TryGetValue(actor, out var audience))
                    return Fail("ACTOR_NOT_JOINED");
                var digest = Digest(kind + "\u001f" + detail);
                if (session.Operations.TryGetValue(op, out var previous))
                    return previous.Digest == digest
                        ? Ok(previous.Event, replayed: true) : Fail("IDEMPOTENCY_CONFLICT");
                if (session.Quarantined) return Fail("SESSION_QUARANTINED");
                if (request.ExpectedRevision != session.Revision)
                    return Fail("REVISION_CONFLICT");
                if (session.Events.Count >= MaxEventsPerSession) return Fail("SESSION_EVENT_CAPACITY");
                session.Revision++;
                var uncertain = kind == "UNCERTAIN_INTENT_SIMULATION";
                var entry = new SessionEvent(session.Revision, kind!, actor, audience, op,
                    // Details are synthetic labels, not provider input or transcript.
                    detail, uncertain ? "UNCERTAIN_SIMULATED_NO_PROVIDER_CALL" : "OBSERVED_LOCAL_ONLY");
                session.Events.Add(entry);
                session.Operations.Add(op, new OperationRecord(digest, entry));
                if (uncertain) session.Quarantined = true;
                changed = true;
                return Ok(entry);
            }
            default:
                return Fail("UNKNOWN_ACTION");
        }
    }

    private static SessionRecord Get(RegistryState state, string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && state.Sessions.TryGetValue(sessionId, out var session)
            ? session : throw new ArgumentException("UNKNOWN_SESSION_ID");

    private static string Audience(string? audience) =>
        audience is "human" or "automation" or "agent"
            ? audience : throw new ArgumentException("INVALID_AUDIENCE");

    private static string Required(string? value, string error, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
            value.Any(char.IsControl)) throw new ArgumentException(error);
        return value;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static SessionReply Ok(object value, bool replayed = false) =>
        new(true, null, value, replayed);
    private static SessionReply Fail(string error) => new(false, error, null, false);
}

public sealed record SessionRequest(
    string Action,
    string? SessionId = null,
    string? ActorId = null,
    string? Audience = null,
    string? OperationId = null,
    string? Title = null,
    string? Kind = null,
    string? Detail = null,
    long? ExpectedRevision = null,
    long? AfterRevision = null
);

public sealed record SessionReply(bool Ok, string? Error, object? Result, bool Replayed);

public sealed class RegistryState
{
    public Dictionary<string, SessionRecord> Sessions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CreationRecord> Creations { get; set; } = new(StringComparer.Ordinal);
}
public sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public long Revision { get; set; }
    public bool Quarantined { get; set; }
    public Dictionary<string, string> Participants { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, OperationRecord> Operations { get; set; } = new(StringComparer.Ordinal);
    public List<SessionEvent> Events { get; set; } = new();
}
public sealed record CreationRecord(string SessionId, string Digest);
public sealed record OperationRecord(string Digest, SessionEvent Event);
public sealed record SessionEvent(
    long Revision, string Kind, string ActorId, string Audience, string? OperationId,
    string Label, string Status
);
