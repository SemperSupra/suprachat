using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupraChat.Core;
using SupraChat.Automation;

static class Test
{
    private static int _n;
    private static void Check(bool ok,string id)
    {
        if (!ok) throw new InvalidOperationException("R46_FAIL_"+id);
        _n++;
    }
    private static void Refuse(Action f,string id)
    {
        try { f(); }
        catch(ArgumentException) { _n++; return; }
        throw new InvalidOperationException("R46_UNSAFE_ACCEPT_"+id);
    }
    // Reuse the actual W12 verifier, not a test double for its MAC/scope/TTL checks.
    // observedPeer is a synthetic independent trusted-bootstrap observation, never
    // a field copied out of the request/grant. This is NOT real OS or device auth.
    private static void CheckSignedGrantComposition()
    {
        var sid=Guid.NewGuid().ToString("N");
        var other=Guid.NewGuid().ToString("N");
        const string owner="verified-owner", guest="verified-guest";
        var now=DateTimeOffset.FromUnixTimeSeconds(1792710000);
        var key=RandomNumberGenerator.GetBytes(32);
        using var grants=new W12ScopedGrantAuthority(key,()=>now);
        CryptographicOperations.ZeroMemory(key);
        var ownerGrant=grants.Issue(owner,"human",sid,new[]{"session.approve"},TimeSpan.FromMinutes(2));
        var guestGrant=grants.Issue(guest,"human",sid,new[]{"session.approve"},TimeSpan.FromMinutes(2));
        var wrongScope=grants.Issue(owner,"human",sid,new[]{"session.read"},TimeSpan.FromMinutes(2));
        var agentGrant=grants.Issue("agent-one","agent",sid,new[]{"session.mutate"},TimeSpan.FromMinutes(2));
        var otherGrant=grants.Issue(owner,"human",other,new[]{"session.approve"},TimeSpan.FromMinutes(2));
        var state=HumanInterventionPolicy.Initial(sid,owner);
        var intent=new HumanControlRequest(sid,0,"verified-owner-pause-001","pause");

        SessionLedgerAdmission Admit(string token,string? observedPeer,string target)
        {
            var admitted=grants.Validate(token,target,"session.approve",claimedActor:observedPeer,
                                         claimedAudience:"human");
            return new SessionLedgerAdmission(admitted.Allowed,admitted.ActorId,admitted.Audience,
                admitted.SessionId,admitted.GrantId,
                HumanBootstrapVerified: admitted.Allowed && observedPeer is not null &&
                                        observedPeer==admitted.ActorId);
        }
        void Denied(string token,string? peer,string target,string name,HumanControlState? checkpoint=null)
        {
            var before=checkpoint??state;
            var result=HumanInterventionPolicy.Apply(before,intent,Admit(token,peer,target));
            Check(result.Status=="AUTHENTICATED_HUMAN_REQUIRED" &&
                result.State==before && !result.IsReplay && !result.StaleWorkFenced &&
                !result.ExternalStopVerified,name);
        }
        Check(grants.Validate(ownerGrant,sid,"session.approve").Allowed,
            "valid_signed_grant_independent_of_peer");
        Denied(ownerGrant,null,sid,"valid_bearer_without_bootstrap");
        Denied(ownerGrant,guest,sid,"stolen_bearer_wrong_observed_peer");
        Denied(guestGrant,guest,sid,"verified_guest_not_designated_owner");
        Denied(wrongScope,owner,sid,"owner_read_scope_cannot_preempt");
        Denied(agentGrant,"agent-one",sid,"agent_mutate_scope_cannot_preempt");
        Denied(otherGrant,owner,sid,"wrong_session_signed_grant");
        Denied(ownerGrant,owner,other,"wrong_session_requested");
        Denied("not-a-grant",owner,sid,"malformed_signed_grant");
        var accepted=HumanInterventionPolicy.Apply(state,intent,Admit(ownerGrant,owner,sid));
        Check(accepted.State.OwnerActorId==owner && accepted.State.Epoch==1 &&
            accepted.State.Phase==HumanControlPhase.PauseRequested &&
            accepted.StaleWorkFenced && !accepted.ExternalStopVerified,
            "signed_owner_and_independent_peer_pause_requested");
        var replay=HumanInterventionPolicy.Apply(accepted.State,intent,Admit(ownerGrant,owner,sid));
        Check(replay.IsReplay && replay.State==accepted.State,"signed_owner_exact_latest_replay");
        Denied(ownerGrant,guest,sid,"stolen_bearer_cannot_replay",accepted.State);
        Denied(guestGrant,guest,sid,"guest_cannot_replay_owner_intent",accepted.State);
        Check(grants.Revoke(ownerGrant),"revoke_existing_synthetic_grant");
        Denied(ownerGrant,owner,sid,"revoked_owner_cannot_replay",accepted.State);
        now=now.AddMinutes(2);
        var expired=grants.Validate(guestGrant,sid,"session.approve",claimedActor:guest);
        Check(!expired.Allowed && expired.Status=="GRANT_EXPIRED_OR_NOT_YET_VALID",
            "independent_signed_grant_expiry");
        Denied(guestGrant,guest,sid,"expired_guest_denied",accepted.State);
        // Actual authenticated host, durable grant revocation, epoch commit,
        // older idempotency receipts and effect reconciliation remain held.
    }

    // Existing W12 verifier + W9 ledger admission/revocation seam.
    // Serialized checkpoint fixture only: not an actual authenticated host,
    // a new durable store, or persistence of the human-control epoch.
    private static void CheckLedgerAdmissionComposition()
    {
        SessionLedgerCheckpoint? checkpoint=null;
        void Capture(SessionLedgerCheckpoint cp) =>
            checkpoint=JsonSerializer.Deserialize<SessionLedgerCheckpoint>(JsonSerializer.Serialize(cp));
        var ledger=new SharedSessionLedger(persist:Capture);
        var a=ledger.Open("r55-policy-open-a-001","automation","A").Session!;
        var b=ledger.Open("r55-policy-open-b-001","automation","B").Session!;
        const string owner="fixture-owner", workerId="fixture-worker";
        var now=DateTimeOffset.FromUnixTimeSeconds(1792710000);
        var key=RandomNumberGenerator.GetBytes(32);
        using var grants=new W12ScopedGrantAuthority(key,()=>now);
        CryptographicOperations.ZeroMemory(key);
        var ownerGrant=grants.Issue(owner,"human",a.Id,new[]{"session.read","session.approve"},TimeSpan.FromMinutes(2));
        var workerGrant=grants.Issue(workerId,"agent",a.Id,new[]{"session.read","session.propose","session.mutate"},TimeSpan.FromMinutes(2));
        var otherGrant=grants.Issue(workerId,"agent",b.Id,new[]{"session.read","session.propose"},TimeSpan.FromMinutes(2));
        var policy=HumanInterventionPolicy.Initial(a.Id,owner);
        SessionLedgerAdmission Admit(string token,string? peer,string target,string scope,string audience)
        {
            var verified=grants.Validate(token,target,scope,claimedActor:peer,claimedAudience:audience);
            return new(verified.Allowed && peer is not null,verified.ActorId,verified.Audience,
                verified.SessionId,verified.GrantId,
                HumanBootstrapVerified: verified.Allowed && audience=="human" && peer is not null &&
                    peer==verified.ActorId);
        }
        int calls=0;
        SessionLedgerProjection<HumanControlFence> Observe(
            SharedSessionLedger host,string token,string? peer,string target,long epoch)
        {
            SessionLedgerAdmission? admitted=null;
            return host.InspectProjectionAdmitted(target,
                ()=>admitted=Admit(token,peer,target,"session.propose","agent"),
                snapshot=>
                {
                    calls++;
                    Check(snapshot.Id==a.Id,"projection_exact_target");
                    return HumanInterventionPolicy.CheckProposal(policy,epoch,admitted!);
                });
        }
        var missingPeer=ledger.InspectProjectionAdmitted(a.Id,
            ()=>Admit(ownerGrant,null,a.Id,"session.read","human"),snapshot=>{calls++;return snapshot.Id;});
        Check(missingPeer.Admission.Status=="ADMISSION_REFUSED" && missingPeer.Projection is null &&
              calls==0,"ledger_rejects_human_bearer_without_peer");
        foreach (var denied in new[]{
            Observe(ledger,workerGrant,"wrong-peer",a.Id,0),
            Observe(ledger,otherGrant,workerId,a.Id,0),
            Observe(ledger,ownerGrant,owner,a.Id,0)})
            Check(denied.Admission.Status=="ADMISSION_REFUSED" && denied.Projection is null &&
                  calls==0,"ledger_denies_wrong_peer_target_or_scope");
        var allowed=Observe(ledger,workerGrant,workerId,a.Id,0);
        Check(allowed.Admission.Status=="OBSERVED" &&
              allowed.Projection is {ProposalMayContinue:true,ToolAuthorityGranted:false} &&
              calls==1,"admitted_projection_not_tool_effect");
        var paused=HumanInterventionPolicy.Apply(policy,new(a.Id,0,"r55-owner-pause-00001","pause"),
            Admit(ownerGrant,owner,a.Id,"session.approve","human"));
        Check(paused.State.Epoch==1 && paused.StaleWorkFenced && !paused.ExternalStopVerified,
            "fixture_policy_pause_not_stop_witness");
        policy=paused.State;
        Check(Observe(ledger,workerGrant,workerId,a.Id,0).Projection is
            {Status:"STALE_EPOCH_FENCED",ProposalMayContinue:false,ToolAuthorityGranted:false},
            "admitted_projection_stale_epoch_refused");
        Check(Observe(ledger,workerGrant,workerId,a.Id,1).Projection is
            {Status:"HUMAN_INTERVENTION_FENCED",ProposalMayContinue:false,ToolAuthorityGranted:false},
            "admitted_projection_paused_epoch_refused");

        // Existing metadata writer does not yet enforce the policy epoch.
        // This tests the admitted metadata journal and durable revocation only.
        var change=new SessionLedgerCommand("set-title",a.Id,"r55-worker-title-0001",a.Version,"A revised");
        Func<SessionLedgerAdmission> admitWorker=()=>Admit(workerGrant,workerId,a.Id,"session.mutate","agent");
        var first=ledger.ExecuteAdmitted(change,admitWorker);
        Check(first.Applied && first.Session?.Version==a.Version+1,"fixture_metadata_writer_applied");
        var replay=ledger.ExecuteAdmitted(change,admitWorker);
        Check(replay.IsReplay && replay.Session==first.Session,"admitted_metadata_replay");
        var verified=admitWorker();
        Check(ledger.RevokeOwnGrant(verified.GrantId!,a.Id,"agent"),"ledger_revocation_committed");
        Check(admitWorker().Allowed,"signature_still_valid_after_ledger_revocation");
        var count=calls;
        var revoked=Observe(ledger,workerGrant,workerId,a.Id,1);
        Check(revoked.Admission.Status=="GRANT_REVOKED" && revoked.Projection is null &&
              calls==count,"revoked_projection_callback_not_executed");
        Check(ledger.ExecuteAdmitted(change,admitWorker).Status=="GRANT_REVOKED",
            "revoked_actor_cannot_read_historical_outcome");
        Check(checkpoint is not null,"existing_checkpoint_contains_revocation");
        var recovered=new SharedSessionLedger(checkpoint,persist:Capture);
        Check(recovered.IsGrantRevoked(verified.GrantId!),"revocation_recovered_from_existing_checkpoint");
        Check(recovered.Inspect(a.Id)==first.Session && recovered.Inspect(b.Id)==b,
            "recovered_metadata_and_other_target_unchanged");
        revoked=Observe(recovered,workerGrant,workerId,a.Id,1);
        Check(revoked.Admission.Status=="GRANT_REVOKED" && revoked.Projection is null &&
              calls==count,"recovered_revocation_blocks_projection");
        Check(recovered.ExecuteAdmitted(change,admitWorker).Status=="GRANT_REVOKED",
            "recovered_revocation_blocks_historical_replay");
    }

    static int Main()
    {
        CheckSignedGrantComposition();
        CheckLedgerAdmissionComposition();
        var session=Guid.NewGuid().ToString("N");
        var other=Guid.NewGuid().ToString("N");
        var grant=Guid.NewGuid().ToString("N");
        var trustedHuman=new SessionLedgerAdmission(true,"verified-human","human",session,grant,true);
        var unverifiedHuman=trustedHuman with {HumanBootstrapVerified=false};
        var otherHuman=trustedHuman with {ActorId="authenticated-guest"};
        var bogusAgent=trustedHuman with {Audience="agent",HumanBootstrapVerified=false};
        var worker=new SessionLedgerAdmission(true,"worker-1","agent",session,
                                              Guid.NewGuid().ToString("N"));
        var automation=worker with {ActorId="automation-1",Audience="automation"};
        var s=HumanInterventionPolicy.Initial(session,"verified-human");
        Check(s.Epoch==0 && s.Phase==HumanControlPhase.Running,"initial");
        Check(HumanInterventionPolicy.CheckProposal(s,0,worker).ProposalMayContinue,
              "agent_policy_proposal");
        Check(HumanInterventionPolicy.CheckProposal(s,0,automation).ProposalMayContinue,
              "automation_policy_proposal");
        Check(!HumanInterventionPolicy.CheckProposal(s,0,worker).ToolAuthorityGranted,
              "separate_tool_authority");
        foreach(var malformed in new string?[]{null,""," ","bad:actor","actor with space","actor\nline","é",new string('a',81)})
        {
            var rejected=HumanInterventionPolicy.CheckProposal(s,0,worker with {ActorId=malformed});
            Check(rejected.Status=="AUTHENTICATED_ACTOR_REQUIRED" &&
                !rejected.ProposalMayContinue && !rejected.ToolAuthorityGranted,"malformed_proposing_actor_refused");
        }

        var guidance=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "synthetic human intent not transported in control receipt"))).ToLowerInvariant();
        var req=new HumanControlRequest(session,0,"human-guidance-op-000001","guide",guidance);
        var unauthorized=HumanInterventionPolicy.Apply(s,req,unverifiedHuman);
        Check(HumanInterventionPolicy.Apply(s,req,otherHuman).Status==
              "AUTHENTICATED_HUMAN_REQUIRED","nonowner_cannot_preempt");
        Check(unauthorized.Status=="AUTHENTICATED_HUMAN_REQUIRED" && unauthorized.State==s,
              "unverified_ui_not_human_authority");
        Check(HumanInterventionPolicy.Apply(s,req,bogusAgent).Status==
              "AUTHENTICATED_HUMAN_REQUIRED","agent_cannot_preempt_as_human");
        Check(HumanInterventionPolicy.Apply(s,req,trustedHuman with {SessionId=other}).Status==
              "AUTHENTICATED_HUMAN_REQUIRED","wrong_session");
        var accepted=HumanInterventionPolicy.Apply(s,req,trustedHuman);
        Check(accepted.Status=="PAUSE_REQUESTED_NOT_VERIFIED" &&
              accepted.State.Epoch==1 && accepted.State.Phase==HumanControlPhase.PauseRequested,
              "human_immediate_guidance_fence");
        Check(accepted.State.GuidanceDigest==guidance && accepted.StaleWorkFenced &&
              !accepted.ExternalStopVerified,"guidance_hash_and_stop_nonclaim");
        Check(HumanInterventionPolicy.CheckProposal(accepted.State,0,worker).Status==
              "STALE_EPOCH_FENCED","old_agent_tool_epoch");
        Check(HumanInterventionPolicy.CheckProposal(accepted.State,1,automation).Status==
              "HUMAN_INTERVENTION_FENCED","new_automation_when_paused");
        Check(HumanInterventionPolicy.CheckProposal(accepted.State,1,worker with {SessionId=other}).Status==
              "AUTHENTICATED_ACTOR_REQUIRED","wrong_session_agent");
        Check(HumanInterventionPolicy.CheckProposal(accepted.State,1,worker with {Allowed=false}).Status==
              "AUTHENTICATED_ACTOR_REQUIRED","missing_worker_admission");

        var replay=HumanInterventionPolicy.Apply(accepted.State,req,trustedHuman);
        Check(replay.IsReplay && replay.State==accepted.State,"same_key_same_request");
        var conflict=HumanInterventionPolicy.Apply(accepted.State,req with {Kind="stop",GuidanceDigest=null},
                                                    trustedHuman);
        Check(conflict.Status=="IDEMPOTENCY_CONFLICT","reuse_key_modified_payload");
        Check(HumanInterventionPolicy.Apply(accepted.State,
              new(session,0,"another-human-00001","stop"),trustedHuman).Status==
              "STALE_HUMAN_INTENT_REQUIRES_REFRESH","concurrent_human_cas");
        Check(HumanInterventionPolicy.Apply(accepted.State,
              new(session,1,"premature-resume-001","resume"),trustedHuman).Status==
              "OWNER_QUIESCENCE_NOT_VERIFIED","resume_before_owner_ack");

        var stop=HumanInterventionPolicy.Apply(accepted.State,
              new(session,1,"human-stop-op-00002","stop"),trustedHuman);
        Check(stop.State.Epoch==2 && stop.State.Phase==HumanControlPhase.StopRequested &&
              !stop.ExternalStopVerified, "human_stop_requested_not_stopped");
        Check(HumanInterventionPolicy.Apply(stop.State,
              new(session,2,"human-override-op-003","guide",guidance),trustedHuman).Status==
              "GUIDANCE_OR_PHASE_REFUSED","cannot_override_unverified_stop");

        // Pure reducer cannot verify executor quiescence. A trusted owner MUST
        // supply the observed Quiescent checkpoint in the real ledger integration.
        var ownerFixture=accepted.State with {Phase=HumanControlPhase.Quiescent};
        var resume=HumanInterventionPolicy.Apply(ownerFixture,
                   new(session,1,"human-resume-op-0004","resume"),trustedHuman);
        Check(resume.State.Phase==HumanControlPhase.Running &&
              resume.State.Epoch==2 && !resume.ExternalStopVerified,
              "synthetic_owner_quiescence_only");
        Check(HumanInterventionPolicy.CheckProposal(resume.State,1,worker).Status==
              "STALE_EPOCH_FENCED","old_worker_after_resume");
        Check(HumanInterventionPolicy.CheckProposal(resume.State,2,worker).ProposalMayContinue &&
              !HumanInterventionPolicy.CheckProposal(resume.State,2,worker).ToolAuthorityGranted,
              "new_worker_proposal_still_no_tool_scope");

        var second=HumanInterventionPolicy.Initial(other,"other-owner");
        Check(second.Epoch==0 && second.Phase==HumanControlPhase.Running,
              "independent_sessions_no_global_stop");

        Refuse(()=>HumanInterventionPolicy.Initial("guest-session","owner"),"invalid_session");
        Refuse(()=>HumanInterventionPolicy.Initial(session,""),"missing_owner");
        Refuse(()=>HumanInterventionPolicy.Initial(session,"attacker:forged"),"invalid_owner");
        Refuse(()=>HumanInterventionPolicy.Apply(
              s with {OwnerActorId="attacker:forged"}, req, trustedHuman),
              "forged_state_owner");
        Refuse(()=>HumanInterventionPolicy.Apply(s,
              new(session,0,"short","pause"),trustedHuman),"invalid_op_id");
        Refuse(()=>HumanInterventionPolicy.Apply(s,
              new(session,0,"valid-request-0001","guide","forged-user-prompt"),trustedHuman),
              "guidance_never_raw_text");
        Refuse(()=>HumanInterventionPolicy.Apply(s,
              new(session,-1,"valid-request-0002","pause"),trustedHuman),"invalid_epoch");

        // Concurrency proof is strictly a synthetic single-writer fixture;
        // the source policy DOES NOT replace the actual W9 transaction lock.
        var gate=new object();
        var race=HumanInterventionPolicy.Initial(session,"verified-human");
        int acceptedCount=0;
        Parallel.For(0,40,i=>{
            lock(gate)
            {
                var o=HumanInterventionPolicy.Apply(race,
                    new(session,0,$"concurrent-human-op-{i:D4}","pause"),trustedHuman);
                if(o.Status=="PAUSE_REQUESTED_NOT_VERIFIED")
                {race=o.State; acceptedCount++;}
            }
        });
        Check(acceptedCount==1 && race.Epoch==1,"synthetic_serialized_cas_race");
        Console.WriteLine($"{{\"schema\":\"suprachat-human-priority-r46/v1\","+
            $"\"status\":\"PASS\",\"checks\":{_n},\"synthetic_single_writer\":true,"+
            "\"actual_executor_stopped\":false,\"remote_identity_proven\":false,"+
            "\"tool_authority_granted\":false,\"production_admitted\":false}");
        return 0;
    }
}
