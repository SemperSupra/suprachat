#!/usr/bin/env node
// W8 PUBLIC synthetic capability-policy RDTE, NOT a deployed identity provider.
// Issuer "verified" claim here is injected by a fixed fixture. Peer credential
// verification, key custody and production host middleware are UNIMPLEMENTED.
import assert from 'node:assert/strict';
import {createHmac, randomBytes, timingSafeEqual} from 'node:crypto';

const schema='suprachat-w8-synthetic-actor-policy/v1';
const actors=[
  {id:'human-operator',role:'human'},
  {id:'scheduled-automation',role:'automation'},
  {id:'agent-worker',role:'agent'}
];
const permissions=Object.freeze({
  human: ['session:read','session:create','session:update','session:approve'],
  automation: ['session:read','session:create','session:update'],
  agent: ['session:read','session:propose']
});
const validActions=new Set(Object.values(permissions).flat());
const receipt=[];
function pass(name){receipt.push({name,status:'PASS'});}
function denied(fn, reason){
  let actual;
  try{fn();}catch(err){actual=err?.message;}
  assert.equal(actual,reason,'Refusal code drift for '+reason);
}
function encode(value){return Buffer.from(JSON.stringify(value)).toString('base64url');}
function decode(base64){
  const buf=Buffer.from(base64,'base64url');
  if(buf.toString('base64url')!==base64)throw Error('NONCANONICAL_TOKEN');
  return JSON.parse(buf.toString('utf8'));
}
class ActorPolicy {
  constructor(){
    this.epoch=0;
    this.secret=randomBytes(32);
    this.consumed=new Set();
    this.operationKeys=new Map();
  }
  issue(attestedActor, {session='*', actions, lifetimeSeconds=120, oneUse=false}, now){
    // The attestedActor is a privileged, pre-verified transport identity.
    // The untrusted HTTP audience label is NOT an accepted input here.
    const actor=actors.find(a=>a.id===attestedActor?.id && a.role===attestedActor?.role);
    if(!actor||attestedActor.peerIdentityVerified!==true)
      throw Error('PEER_IDENTITY_NOT_VERIFIED');
    if(!Array.isArray(actions)||actions.length===0||
       actions.some(x=>!validActions.has(x)||!permissions[actor.role].includes(x)))
      throw Error('ACTION_NOT_PERMITTED_FOR_ACTOR');
    if(!Number.isInteger(lifetimeSeconds)||lifetimeSeconds<1||lifetimeSeconds>300)
      throw Error('INVALID_GRANT_LIFETIME');
    if(!/^[a-z0-9-]{1,64}$/i.test(session)&&session!=='*')
      throw Error('INVALID_SESSION_SCOPE');
    const claim={v:schema,actor:actor.id,role:actor.role,
      actions:[...new Set(actions)].sort(),session,
      issuedAt:now,expiresAt:now+lifetimeSeconds*1000,
      nonce:randomBytes(16).toString('hex'),epoch:this.epoch,oneUse};
    const payload=encode(claim);
    const mac=createHmac('sha256',this.secret).update(payload).digest('base64url');
    return payload+'.'+mac;
  }
  authorize(token, {action,session,claimedAudience,operationKey}, now){
    if(typeof token!=='string'||token.length>4096||token.split('.').length!==2)
      throw Error('INVALID_CAPABILITY');
    const [encoded,signature]=token.split('.');
    let expected;
    try{expected=createHmac('sha256',this.secret).update(encoded).digest();
      const supplied=Buffer.from(signature,'base64url');
      if(supplied.length!==32||!timingSafeEqual(supplied,expected))
        throw Error('INVALID_CAPABILITY');
    }catch{throw Error('INVALID_CAPABILITY');}
    let claim;
    try{claim=decode(encoded);}catch{throw Error('INVALID_CAPABILITY');}
    if(claim.v!==schema||claim.epoch!==this.epoch)
      throw Error('REVOKED_CAPABILITY');
    if(typeof claim.expiresAt!=='number'||now<claim.issuedAt||now>=claim.expiresAt)
      throw Error('EXPIRED_CAPABILITY');
    if(!actors.some(a=>a.id===claim.actor&&a.role===claim.role)||
       !permissions[claim.role].includes(action)||
       !Array.isArray(claim.actions)||!claim.actions.includes(action))
      throw Error('ACTION_NOT_PERMITTED_FOR_ACTOR');
    if(claim.session!=='*'&&claim.session!==session)
      throw Error('SESSION_SCOPE_DENIED');
    // A caller-provided "audience" string never controls authorization.
    // It may be recorded separately as untrusted request metadata.
    void claimedAudience;
    if(claim.oneUse && this.consumed.has(claim.nonce))
      throw Error('CAPABILITY_ALREADY_CONSUMED');
    if(operationKey!==undefined){
      if(typeof operationKey!=='string'||operationKey.length<12)
        throw Error('INVALID_OPERATION_KEY');
      const previously=this.operationKeys.get(operationKey);
      // Globally unique keys, scoped to derived issuer-signed actor:
      // an agent cannot replay human's key or change its action/session.
      if(previously&&
        (previously.actor!==claim.actor||previously.session!==session||
         previously.action!==action))
        throw Error('CROSS_ACTOR_IDEMPOTENCY_KEY_CONFLICT');
      this.operationKeys.set(operationKey,
        {actor:claim.actor,session,action});
    }
    if(claim.oneUse)this.consumed.add(claim.nonce);
    return {actorId:claim.actor,role:claim.role,action,session,
      claimedAudienceIgnored:true,transportPeerVerifiedHere:false,
      providerSubmissionAllowed:false,terminalExecutionAllowed:false};
  }
  revokeAll(){this.epoch++;this.operationKeys.clear();this.consumed.clear();}
}
const policy=new ActorPolicy();
const now=1_790_000_000_000;
const trusted=(actor)=>({...actor,peerIdentityVerified:true});
denied(()=>policy.issue({...actors[0],peerIdentityVerified:false},
  {actions:['session:approve']},now),'PEER_IDENTITY_NOT_VERIFIED');
pass('no-token-before-independent-peer-identity-check');

const h=policy.issue(trusted(actors[0]),{session:'session-a',actions:['session:read','session:approve'],oneUse:true},now);
const a=policy.issue(trusted(actors[1]),{session:'session-a',actions:['session:read','session:update']},now);
const g=policy.issue(trusted(actors[2]),{session:'session-a',actions:['session:read','session:propose']},now);
pass('issuer-signed-per-actor-distinct-least-privilege-capabilities');
const human=policy.authorize(h,{action:'session:approve',session:'session-a',
  claimedAudience:'agent',operationKey:'w8-human-approve-000001'},now+1);
assert.equal(human.actorId,'human-operator');
assert.equal(human.claimedAudienceIgnored,true);
pass('forged-audience-label-cannot-change-derived-actor');
denied(()=>policy.authorize(h,{action:'session:approve',session:'session-a'},now+2),
  'CAPABILITY_ALREADY_CONSUMED');
pass('one-use-human-review-grant-not-replayable');

denied(()=>policy.authorize(a,{action:'session:approve',session:'session-a'},now+1),
  'ACTION_NOT_PERMITTED_FOR_ACTOR');
denied(()=>policy.authorize(g,{action:'session:update',session:'session-a'},now+1),
  'ACTION_NOT_PERMITTED_FOR_ACTOR');
pass('automation-and-agent-cannot-impersonate-reviewer');
denied(()=>policy.authorize(a,{action:'session:update',session:'session-b'},now+1),
  'SESSION_SCOPE_DENIED');
pass('wrong-session-capability-refused');

const changed=a.slice(0,-5)+'ABCDE';
denied(()=>policy.authorize(changed,{action:'session:update',session:'session-a'},now+1),
  'INVALID_CAPABILITY');
denied(()=>policy.authorize('',{action:'session:read',session:'session-a'},now+1),
  'INVALID_CAPABILITY');
pass('altered-and-missing-bearer-tokens-refused');

const autoAuthorized=policy.authorize(a,
  {action:'session:update',session:'session-a',operationKey:'w8-actor-key-00000001'},now+1);
assert.equal(autoAuthorized.actorId,'scheduled-automation');
denied(()=>policy.authorize(g,
  {action:'session:propose',session:'session-a',operationKey:'w8-actor-key-00000001'},now+1),
  'CROSS_ACTOR_IDEMPOTENCY_KEY_CONFLICT');
pass('operation-key-reuse-from-different-actor-refused');

denied(()=>policy.authorize(a,
  {action:'session:read',session:'session-a'},now+120_000),'EXPIRED_CAPABILITY');
pass('expired-delegation-refused');
policy.revokeAll();
denied(()=>policy.authorize(a,{action:'session:read',session:'session-a'},now+3),
  'REVOKED_CAPABILITY');
pass('revocation-epoch-refuses-outstanding-grants');

const independent=new ActorPolicy();
denied(()=>independent.authorize(g,{action:'session:read',session:'session-a'},now+1),
  'INVALID_CAPABILITY');
pass('distinct-host-secret-does-not-implicitly-trust-old-host-grants');

const result={
  schema,checks:receipt,passed:receipt.length,
  source:'PUBLIC_SYNTHETIC_POLICY_ONLY',
  peer_identity_verified_in_test:false,
  os_peer_credentials_integrated:false,
  production_key_custody_qualified:false,
  w6_http_host_integrated:false,
  browser_prompt_submitted:false,
  agent_can_override_human_review:false,
  authenticated_audience_identity_in_w6:false,
  production_identity_admitted:false
};
console.log(JSON.stringify(result,null,2));
