using System.Security.Cryptography;
using System.Text.Json;
using SupraChat.ActorAuthRdte;

internal static class Program
{
    private static void Expect(bool condition,string failure)
    {
        if(!condition)throw new InvalidOperationException("W8_ASSERT_"+failure);
    }
    public static int Main()
    {
        try
        {
            var fakeNow=DateTimeOffset.FromUnixTimeSeconds(1792710000);
            using var authority=new ActorGrantAuthority(RandomNumberGenerator.GetBytes(32),()=>fakeNow);
            const string a="synthetic-A",b="synthetic-B";
            var human=authority.Issue("human-one","human",a,
                new[]{"session.read","session.approve","session.mutate"},TimeSpan.FromMinutes(10));
            var agent=authority.Issue("agent-one","agent",a,
                new[]{"session.read","session.propose"},TimeSpan.FromMinutes(5));
            var automation=authority.Issue("automation-one","automation",b,
                new[]{"session.read","session.mutate"},TimeSpan.FromMinutes(5));
            var h=authority.Validate(human,a,"session.approve");
            Expect(h.Allowed && h.ActorId=="human-one" && h.Audience=="human","SIGNED_HUMAN");
            var g=authority.Validate(agent,a,"session.propose");
            Expect(g.Allowed && g.ActorId=="agent-one" && g.Audience=="agent","SIGNED_AGENT");
            var auto=authority.Validate(automation,b,"session.mutate");
            Expect(auto.Allowed && auto.ActorId=="automation-one","SIGNED_AUTOMATION");
            Expect(!authority.Validate(agent,a,"session.approve").Allowed,"AGENT_APPROVAL_DENIED");
            Expect(!authority.Validate(agent,a,"session.mutate").Allowed,"AGENT_ESCALATION_DENIED");
            Expect(!authority.Validate(automation,a,"session.mutate").Allowed,"WRONG_SESSION");
            Expect(!authority.Validate(human,b,"session.approve").Allowed,"HUMAN_WRONG_SESSION");
            Expect(authority.Validate(agent,a,"session.read",claimedActor:"human-one").Status
                =="CLAIMED_IDENTITY_MISMATCH","FORGED_ACTOR");
            Expect(authority.Validate(agent,a,"session.read",claimedAudience:"human").Status
                =="CLAIMED_IDENTITY_MISMATCH","FORGED_AUDIENCE");
            Expect(!authority.Validate("any-shared-bearer",a,"session.read").Allowed,
                "LEGACY_BEARER_CANNOT_IMPERSONATE");
            var parts=agent.Split('.');
            parts[1]=(parts[1][0]=='A'?'B':'A')+parts[1][1..];
            Expect(!authority.Validate(string.Join(".",parts),a,"session.read").Allowed,
                "TAMPERED_SIGNATURE");
            var agentClaims=agent.Split('.');
            agentClaims[0]=(agentClaims[0][0]=='A'?'B':'A')+agentClaims[0][1..];
            Expect(!authority.Validate(string.Join(".",agentClaims),a,"session.read").Allowed,
                "TAMPERED_CLAIMS");
            try
            {
                authority.Issue("malicious-agent","agent",a,new[]{"session.approve"},
                    TimeSpan.FromMinutes(2));
                throw new InvalidOperationException("W8_BAD_AGENT_APPROVAL_ISSUED");
            }
            catch(ArgumentException) { }
            try
            {
                authority.Issue("bad-ttl","human",a,new[]{"session.read"},
                    TimeSpan.FromHours(25));
                throw new InvalidOperationException("W8_UNBOUNDED_GRANT_ISSUED");
            }
            catch(ArgumentException) { }
            var signedBeforeRevoke=authority.Validate(agent,a,"session.read");
            Expect(signedBeforeRevoke.Allowed,"VALID_BEFORE_REVOCATION");
            Expect(authority.Revoke(agent),"REVOKE_ADMITTED");
            Expect(authority.Validate(agent,a,"session.read").Status=="GRANT_REVOKED",
                "REVOKED_DENIED");
            var oneUse=authority.Issue("automation-short","automation",b,
                new[]{"session.read"},TimeSpan.FromSeconds(2));
            Expect(authority.Validate(oneUse,b,"session.read").Allowed,"SHORT_LIVED_START");
            fakeNow=fakeNow.AddSeconds(3);
            Expect(authority.Validate(oneUse,b,"session.read").Status==
                "GRANT_EXPIRED_OR_NOT_YET_VALID","EXPIRED_DENIED");
            var reads=Enumerable.Range(0,200).Select(i=>Task.Run(() =>
                authority.Validate(human,a,"session.read"))).ToArray();
            Task.WaitAll(reads);
            Expect(reads.All(t=>t.Result.Allowed),"PARALLEL_SHARED_SESSION_READER");
            Expect(!authority.Validate(human,b,"session.read").Allowed,
                "CROSS_SESSION_LEAK");
            Console.WriteLine(JsonSerializer.Serialize(new {
                schema=ActorGrantAuthority.Schema,
                result="PASS", human_signed_authority=true,
                automation_signed_authority=true, agent_signed_authority=true,
                forged_claims_denied=true, session_scope_isolated=true,
                elevated_role_denied=true, token_tampering_denied=true,
                revocation_enforced=true, expiry_enforced=true,
                parallel_reads=200, private_key_in_output=false,
                real_actor_identity_authenticated=false, host_integrated=false,
                credentials_exported=false, provider_calls=0
            }));
            return 0;
        }
        catch(Exception)
        {
            // No token, secret, actor identifier, or payload logged on failure.
            Console.Error.WriteLine("W8_SYNTHETIC_QUALIFICATION_FAILED");
            return 1;
        }
    }
}
