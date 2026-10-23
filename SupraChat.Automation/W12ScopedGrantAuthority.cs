using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.Automation;

/// <summary>
/// W8 experimental *delegation token* verifier. No OS peer identity, issuer
/// admission, credential vault or W6 host integration is provided.
/// Possession of a valid token is NOT proof of the claimed human's identity.
/// </summary>
public sealed class W12ScopedGrantAuthority : IDisposable
{
    public const string Schema = "suprachat-w12-host-metadata-grant-rdte/v1";
    private static readonly HashSet<string> AllowedScopes =
        new(StringComparer.Ordinal) { "session.read", "session.propose",
            "session.mutate", "session.approve", "session.create" };
    private readonly byte[] _key;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<string> _revokedIds = new(StringComparer.Ordinal);
    private bool _disposed;

    public W12ScopedGrantAuthority(ReadOnlySpan<byte> key, Func<DateTimeOffset>? clock = null)
    {
        if(key.Length<32) throw new ArgumentException("INSUFFICIENT_SIGNING_ENTROPY");
        _key=key.ToArray();
        _clock=clock??(()=>DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A trusted, out-of-band issuer would make this call after human approval.
    /// Never expose this API as an unguarded HTTP/agent tool.
    /// </summary>
    public string Issue(string actorId, string audience, string sessionId,
        IReadOnlyCollection<string> scopes, TimeSpan duration)
    {
        EnsureOpen();
        if (!ValidActor(actorId) || !ValidSession(sessionId) ||
            audience is not ("human" or "automation" or "agent"))
            throw new ArgumentException("INVALID_ISSUANCE_IDENTITY");
        if(duration<=TimeSpan.Zero || duration>TimeSpan.FromMinutes(30))
            throw new ArgumentException("INVALID_GRANT_TTL");
        if(scopes.Count is <1 or >4 ||
           scopes.Any(s=>!AllowedScopes.Contains(s)) ||
           (audience!="human" && scopes.Contains("session.approve")))
            throw new ArgumentException("SCOPE_NOT_ISSUABLE");
        var claims = new W12ActorClaims(Schema,actorId,audience,sessionId,
            scopes.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
            _clock().ToUnixTimeSeconds(), _clock().Add(duration).ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("N"));
        var encoded=Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature=Base64Url(HMACSHA256.HashData(_key,Encoding.ASCII.GetBytes(encoded)));
        return encoded+"."+signature;
    }

    public W12Admission Validate(string? token,string? requestedSession,
        string? requestedAction, string? claimedActor=null,string? claimedAudience=null)
    {
        EnsureOpen();
        if(string.IsNullOrWhiteSpace(token)||token.Length>2048)
            return Deny("MISSING_OR_INVALID_CAPABILITY");
        var parts=token.Split('.');
        if(parts.Length!=2) return Deny("INVALID_CAPABILITY_FORMAT");
        byte[] supplied;
        try { supplied=FromBase64Url(parts[1]); }
        catch(FormatException) {return Deny("INVALID_SIGNATURE_FORMAT");}
        var mac=HMACSHA256.HashData(_key,Encoding.ASCII.GetBytes(parts[0]));
        if(supplied.Length!=mac.Length ||
           !CryptographicOperations.FixedTimeEquals(mac,supplied))
            return Deny("INVALID_SIGNATURE");
        W12ActorClaims? claims;
        try
        {
            claims=JsonSerializer.Deserialize<W12ActorClaims>(FromBase64Url(parts[0]));
        }
        catch(Exception ex) when(ex is FormatException or JsonException)
        {return Deny("INVALID_CLAIMS");}
        if(claims is null || claims.Schema!=Schema ||
           !ValidActor(claims.ActorId)||!ValidSession(claims.SessionId) ||
           claims.Audience is not ("human" or "automation" or "agent") ||
           claims.Scopes is null || claims.Scopes.Length is <1 or >4 ||
           claims.Scopes.Any(s=>!AllowedScopes.Contains(s)) ||
           (claims.Audience!="human" && claims.Scopes.Contains("session.approve")) ||
           !Guid.TryParseExact(claims.GrantId,"N",out _))
            return Deny("INVALID_CLAIMS");
        if(_revokedIds.Contains(claims.GrantId))
            return Deny("GRANT_REVOKED");
        var now=_clock().ToUnixTimeSeconds();
        if(now<claims.IssuedAt || now>=claims.ExpiresAt ||
           claims.ExpiresAt-claims.IssuedAt>1800)
            return Deny("GRANT_EXPIRED_OR_NOT_YET_VALID");
        if(!string.Equals(claims.SessionId,requestedSession,StringComparison.Ordinal))
            return Deny("WRONG_SESSION");
        if(!AllowedScopes.Contains(requestedAction??"") ||
           !claims.Scopes.Contains(requestedAction!,StringComparer.Ordinal))
            return Deny("SCOPE_DENIED");
        // The caller's advertised audience or ID never upgrades privileges:
        // mismatch is rejected and signed claims remain authoritative.
        if(claimedActor is not null && claimedActor!=claims.ActorId ||
           claimedAudience is not null && claimedAudience!=claims.Audience)
            return Deny("CLAIMED_IDENTITY_MISMATCH");
        return new W12Admission(true,"ADMITTED",claims.ActorId,claims.Audience,
            claims.SessionId,claims.GrantId);
    }

    public bool Revoke(string token)
    {
        EnsureOpen();
        // Revocation needs the already-verified signature; do not trust
        // unchecked user-provided grant IDs.
        var parts=token.Split('.');
        if(parts.Length!=2)return false;
        try
        {
            var sig=FromBase64Url(parts[1]);
            var actual=HMACSHA256.HashData(_key,Encoding.ASCII.GetBytes(parts[0]));
            if(sig.Length!=actual.Length ||
               !CryptographicOperations.FixedTimeEquals(sig,actual))return false;
            var claims=JsonSerializer.Deserialize<W12ActorClaims>(FromBase64Url(parts[0]));
            return claims?.Schema==Schema && Guid.TryParseExact(claims.GrantId,"N",out _) &&
                _revokedIds.Add(claims.GrantId);
        }
        catch(Exception ex) when(ex is FormatException or JsonException) {return false;}
    }

    private static bool ValidActor(string? s)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=80&&
        s.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-');
    private static bool ValidSession(string? s)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=80&&
        s.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-');
    private static W12Admission Deny(string code)=>new(false,code,null,null,null,null);
    private void EnsureOpen() {if(_disposed)throw new ObjectDisposedException(nameof(W12ScopedGrantAuthority));}
    private static string Base64Url(byte[] input)=>
        Convert.ToBase64String(input).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] FromBase64Url(string input)
    {
        var raw=input.Replace('-','+').Replace('_','/');
        return Convert.FromBase64String(raw.PadRight(raw.Length+(4-raw.Length%4)%4,'='));
    }
    public void Dispose()
    {
        if(_disposed)return;
        CryptographicOperations.ZeroMemory(_key);
        _revokedIds.Clear();
        _disposed=true;
    }
}
public sealed record W12ActorClaims(string Schema,string ActorId,string Audience,string SessionId,
    string[] Scopes,long IssuedAt,long ExpiresAt,string GrantId);
public sealed record W12Admission(bool Allowed,string Status,string? ActorId,string? Audience,
    string? SessionId,string? GrantId);
