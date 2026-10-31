using System.Security.Cryptography;
using System.Text.Json;

namespace SupraChat.Core;

public sealed record SessionLedgerAdmission(bool Allowed, string? ActorId, string? Audience,
    string? SessionId, string? GrantId, bool HumanBootstrapVerified = false);
public sealed record SessionLedgerCommand(string Operation, string SessionId, string Key,
    long ExpectedVersion = 0, string? Value = null, string? ConversationUrl = null, string? WorkspaceKey = null);
public sealed record SessionLedgerProjection<T>(SessionLedgerResult Admission, T? Projection);

public sealed partial class SharedSessionLedger
{
    public const int MaxWorkbenchVisibleSessions = SessionWorkbench.MaximumSessions;

    /// <summary>Copy a bounded read-only projection under the admission/revocation gate.
    /// Trusted callbacks must not block on external I/O or take locks that reenter the ledger.</summary>
    public SessionLedgerProjection<T> InspectProjectionAdmitted<T>(string sessionId,
        Func<SessionLedgerAdmission> admit, Func<SessionLedgerState, T> copyProjection)
    {
        lock (_gate)
        {
            var result = ExecuteAdmitted(new("inspect", sessionId, "unused-inspect-key"), admit);
            return new(result, result.Status == "OBSERVED" ? copyProjection(result.Session!) : default);
        }
    }

    /// <summary>
    /// Copy a bounded read-only inventory projection under the admission/revocation gate.
    /// Requires a verified sessionId == "inventory" grant so unpaired callers cannot enumerate sessions.
    /// </summary>
    public (SessionLedgerResult Admission, SessionLedgerInventory? Inventory) InspectInventoryAdmitted(
        Func<SessionLedgerAdmission> admit)
    {
        lock (_gate)
        {
            var result = ExecuteAdmitted(new("list", "inventory", "unused-list-key"), admit);
            return (result, result.Status == "OBSERVED" ? InspectAll() : null);
        }
    }

    /// <summary>
    /// Trusted-host entry point, not caller-supplied identity. The host callback verifies its
    /// observed peer and grant on every call while the existing W6 mutation gate is held.
    /// All mutations still use Open/Change/RevokeOwnGrant and the same checkpoint authority.
    /// Unverified "human" claims are refused; human-owner operations require HumanBootstrapVerified=true.
    /// </summary>
    public SessionLedgerResult ExecuteAdmitted(SessionLedgerCommand command, Func<SessionLedgerAdmission> admit)
    {
        lock (_gate)
        {
            var grant = admit();
            var validAudience = grant.Audience is "automation" or "agent" && !grant.HumanBootstrapVerified ||
                grant.Audience == "human" && grant.HumanBootstrapVerified;
            if (!grant.Allowed || string.IsNullOrWhiteSpace(grant.ActorId) || grant.ActorId.Length > 80 ||
                grant.ActorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
                !validAudience || grant.SessionId != command.SessionId ||
                !Guid.TryParseExact(grant.GrantId, "N", out _))
                return new(Schema, false, false, "ADMISSION_REFUSED", null, _sequence);
            if (_revokedGrants.Contains(grant.GrantId))
                return new(Schema, false, false, "GRANT_REVOKED", null, _sequence);
            if (command.Operation == "list" && command.SessionId == "inventory")
                return new(Schema, false, false, "OBSERVED", null, _sequence);
            if (command.Operation == "inspect")
                return new(Schema, false, false, _sessions.ContainsKey(command.SessionId) ? "OBSERVED" : "SESSION_NOT_FOUND",
                    _sessions.GetValueOrDefault(command.SessionId), _sequence);
            if (command.Operation == "revoke-self")
            {
                RevokeOwnGrant(grant.GrantId, command.SessionId, grant.Audience!);
                return new(Schema, false, false, "REVOKED", null, _sequence);
            }
            ValidateIdempotencyKey(command.Key);
            var key = "peer-" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new[] { "SUPRACHAT_PEER_OPERATION_V1", grant.ActorId, command.Key })));
            if (command.Operation == "open" && command.SessionId == "create-only")
            {
                // Externally admitted session creation enforces the 16-visible-session workbench cap
                // so no 17th session can be created that LedgerWorkbenchProjection would refuse.
                if (!_operations.ContainsKey(key) && _sessions.Count >= MaxWorkbenchVisibleSessions)
                {
                    ValidateTitle(command.Value);
                    var normalizedUrl = NormalizeConversation(command.ConversationUrl);
                    var workspace = NormalizeWorkspace(command.WorkspaceKey);
                    var fingerprint = Fingerprint("open", grant.Audience!, null, 0, command.Value, normalizedUrl, workspace);
                    if (_operations.Count >= MaxIdempotencyRecords)
                        return new(Schema, false, false, "IDEMPOTENCY_CAPACITY_REACHED", null, _sequence);
                    return Cache(key, fingerprint,
                        new(Schema, false, false, "SESSION_CAPACITY_REACHED", null, _sequence));
                }
                return Open(key, grant.Audience!, command.Value!, command.ConversationUrl, command.WorkspaceKey, signedGrantId: grant.GrantId);
            }
            return Change(key, grant.Audience!, command.SessionId, command.ExpectedVersion,
                command.Operation, command.Value, grant.GrantId);
        }
    }
}