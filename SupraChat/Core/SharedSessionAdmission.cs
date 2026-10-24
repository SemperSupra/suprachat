using System.Security.Cryptography;
using System.Text.Json;

namespace SupraChat.Core;

public sealed record SessionLedgerAdmission(bool Allowed, string? ActorId, string? Audience,
    string? SessionId, string? GrantId);
public sealed record SessionLedgerCommand(string Operation, string SessionId, string Key,
    long ExpectedVersion = 0, string? Value = null);
public sealed record SessionLedgerProjection<T>(SessionLedgerResult Admission, T? Projection);

public sealed partial class SharedSessionLedger
{
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
    /// Trusted-host entry point, not caller-supplied identity. The host callback verifies its
    /// observed peer and grant on every call while the existing W6 mutation gate is held.
    /// All mutations still use Open/Change/RevokeOwnGrant and the same checkpoint authority.
    /// </summary>
    public SessionLedgerResult ExecuteAdmitted(SessionLedgerCommand command, Func<SessionLedgerAdmission> admit)
    {
        lock (_gate)
        {
            var grant = admit();
            if (!grant.Allowed || string.IsNullOrWhiteSpace(grant.ActorId) || grant.ActorId.Length > 80 ||
                grant.ActorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
                grant.Audience is not ("automation" or "agent") || grant.SessionId != command.SessionId ||
                !Guid.TryParseExact(grant.GrantId, "N", out _))
                return new(Schema, false, false, "ADMISSION_REFUSED", null, _sequence);
            if (_revokedGrants.Contains(grant.GrantId))
                return new(Schema, false, false, "GRANT_REVOKED", null, _sequence);
            if (command.Operation == "inspect")
                return new(Schema, false, false, _sessions.ContainsKey(command.SessionId) ? "OBSERVED" : "SESSION_NOT_FOUND",
                    _sessions.GetValueOrDefault(command.SessionId), _sequence);
            if (command.Operation == "revoke-self")
            {
                RevokeOwnGrant(grant.GrantId, command.SessionId, grant.Audience);
                return new(Schema, false, false, "REVOKED", null, _sequence);
            }
            ValidateIdempotencyKey(command.Key);
            var key = "peer-" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new[] { "SUPRACHAT_PEER_OPERATION_V1", grant.ActorId, command.Key })));
            if (command.Operation == "open" && command.SessionId == "create-only")
                return Open(key, grant.Audience, command.Value!, signedGrantId: grant.GrantId);
            return Change(key, grant.Audience, command.SessionId, command.ExpectedVersion,
                command.Operation, command.Value, grant.GrantId);
        }
    }
}
