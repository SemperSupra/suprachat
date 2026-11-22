using System.Security.Cryptography;
using System.Text;
using SupraChat.Core;

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
    static int Main()
    {
        var session=Guid.NewGuid().ToString("N");
        var other=Guid.NewGuid().ToString("N");
        var grant=Guid.NewGuid().ToString("N");
        var trustedHuman=new SessionLedgerAdmission(true,"verified-human","human",session,grant,true);
        var unverifiedHuman=trustedHuman with {HumanBootstrapVerified=false};
        var bogusAgent=trustedHuman with {Audience="agent",HumanBootstrapVerified=false};
        var worker=new SessionLedgerAdmission(true,"worker-1","agent",session,
                                              Guid.NewGuid().ToString("N"));
        var automation=worker with {ActorId="automation-1",Audience="automation"};
        var s=HumanInterventionPolicy.Initial(session);
        Check(s.Epoch==0 && s.Phase==HumanControlPhase.Running,"initial");
        Check(HumanInterventionPolicy.CheckProposal(s,0,worker).ProposalMayContinue,
              "agent_policy_proposal");
        Check(HumanInterventionPolicy.CheckProposal(s,0,automation).ProposalMayContinue,
              "automation_policy_proposal");
        Check(!HumanInterventionPolicy.CheckProposal(s,0,worker).ToolAuthorityGranted,
              "separate_tool_authority");

        var guidance=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "synthetic human intent not transported in control receipt"))).ToLowerInvariant();
        var req=new HumanControlRequest(session,0,"human-guidance-op-000001","guide",guidance);
        var unauthorized=HumanInterventionPolicy.Apply(s,req,unverifiedHuman);
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

        var second=HumanInterventionPolicy.Initial(other);
        Check(second.Epoch==0 && second.Phase==HumanControlPhase.Running,
              "independent_sessions_no_global_stop");

        Refuse(()=>HumanInterventionPolicy.Initial("guest-session"),"invalid_session");
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
        var race=HumanInterventionPolicy.Initial(session);
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
