#!/usr/bin/env node
// W12 synthetic research only. One W6 host, no real provider, no OS identity.
import assert from 'node:assert/strict';
import {createHmac,randomBytes} from 'node:crypto';
import {spawn} from 'node:child_process';
import {createServer} from 'node:net';
import {mkdtemp,rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dll=path.join(root,'SupraChat.Automation','bin','Release','net10.0','SupraChat.Automation.dll');
const dir=await mkdtemp(path.join(os.tmpdir(),'suprachat-w9-owned-w12-'));
const store=path.join(dir,'w12.sqlite');
const bearer=randomBytes(32).toString('hex');
const signingKey=randomBytes(32);
const hostEnv={...process.env,SUPRACHAT_LEDGER_TOKEN:bearer,
  SUPRACHAT_LEDGER_STORE:store,SUPRACHAT_W9_DISPOSABLE_ONLY:'yes',
  SUPRACHAT_W9_CREATE_ONCE:'no',
  SUPRACHAT_W12_SCOPED_WRITES:'synthetic',
  SUPRACHAT_W12_SIGNING_KEY:signingKey.toString('base64')};
const clientEnv={...hostEnv};
delete clientEnv.SUPRACHAT_W12_SIGNING_KEY;
delete clientEnv.SUPRACHAT_W12_SCOPED_WRITES;
let host,port;
const stages=[];
function record(label){stages.push(label)}
async function freePort(){return await new Promise((resolve,reject)=>{
  const srv=createServer();srv.once('error',reject);
  srv.listen(0,'127.0.0.1',()=>{const p=srv.address().port;srv.close(()=>resolve(p))});
})}
function grant(actor,audience,session,scopes,{issued=Date.now()/1000,ttl=250}={}){
  const claims={
    Schema:'suprachat-w12-host-metadata-grant-rdte/v1',
    ActorId:actor,Audience:audience,SessionId:session,
    Scopes:scopes,IssuedAt:Math.floor(issued),
    ExpiresAt:Math.floor(issued+ttl),GrantId:randomBytes(16).toString('hex')
  };
  const encoded=Buffer.from(JSON.stringify(claims)).toString('base64url');
  const signature=createHmac('sha256',signingKey).update(encoded).digest('base64url');
  return encoded+'.'+signature;
}
async function launch(initial=false) {
  port=await freePort();
  host=spawn('dotnet',[dll,'session-ledger-host','--port',String(port)],{
    cwd:root,env:{...hostEnv,SUPRACHAT_W9_CREATE_ONCE:initial?'yes':'no'},
    stdio:['ignore','pipe','pipe'],windowsHide:true});
  await new Promise((resolve,reject)=>{
    let out='',err='',settled=false;
    const t=setTimeout(()=>fail(Error('HOST_READY_TIMEOUT')),14000);
    function fail(e){if(!settled){settled=true;clearTimeout(t);reject(e)}}
    host.once('error',fail);
    host.once('exit',c=>fail(Error('HOST_EXIT_'+c)));
    host.stderr.on('data',b=>{err+=b;if(err.length>20000)fail(Error('HOST_LOG_OVERFLOW'))});
    host.stdout.on('data',b=>{
      out+=b;
      if(out.includes(bearer)||out.includes(signingKey.toString('base64')))
        return fail(Error('SECRET_EXPOSED'));
      const line=out.split(/\r?\n/).slice(0,-1).find(x=>x.startsWith('{"schema":'));
      if(!line)return;
      let ready;
      try{ready=JSON.parse(line)}catch{return fail(Error('READY_BAD_JSON'))}
      if(ready.bound_address!=='127.0.0.1'||
         ready.synthetic_scoped_write_enforcement!==true||
         ready.common_bearer_grants_mutation!==false||
         ready.authenticated_os_peer_identity!==false||
         ready.authenticated_audience_identity!==false||
         ready.production_durable_admission!==false)
         return fail(Error('UNSAFE_AUTHORITY_CLAIM'));
      settled=true;clearTimeout(t);resolve();
    });
  });
}
async function stop(){
  if(!host)return;const p=host;host=null;
  if(p.exitCode!==null)return;
  await new Promise(resolve=>{
    const t=setTimeout(()=>{p.kill('SIGKILL');resolve()},6000);
    p.once('exit',()=>{clearTimeout(t);resolve()});
    p.kill();
  });
}
async function request(route,body,cap,token=bearer){
  const headers={'Authorization':'Bearer '+token};
  if(body)headers['Content-Type']='application/json';
  if(cap)headers['X-SupraChat-Actor-Capability']=cap;
  const ctrl=new AbortController();const timeout=setTimeout(()=>ctrl.abort(),7000);
  try{
    const response=await fetch('http://127.0.0.1:'+port+route,{
      method:body?'POST':'GET',headers,body:body?JSON.stringify(body):undefined,
      signal:ctrl.signal});
    const raw=await response.text();
    assert(raw.length<131072);
    assert(!raw.includes(bearer) && !raw.includes(signingKey.toString('base64')));
    return {status:response.status,payload:raw?JSON.parse(raw):null};
  }finally{clearTimeout(timeout)}
}
async function machine(method,body,cap){
  return await new Promise((resolve,reject)=>{
    const env={...clientEnv};
    if(cap)env.SUPRACHAT_W12_ACTOR_GRANT=cap;
    const cli=spawn('dotnet',[dll,'stdio'],{
      cwd:root,env,stdio:['pipe','pipe','pipe'],windowsHide:true});
    let stdout='',stderr='';
    const t=setTimeout(()=>{cli.kill();reject(Error('MACHINE_TIMEOUT'))},14000);
    cli.stdout.on('data',b=>{stdout+=b;if(stdout.length>150000)cli.kill()});
    cli.stderr.on('data',b=>{stderr+=b;if(stderr.length>8000)cli.kill()});
    cli.once('error',e=>{clearTimeout(t);reject(e)});
    cli.once('exit',code=>{
      clearTimeout(t);
      if(code!==0)return reject(Error('MACHINE_PROCESS_ERROR_'+code));
      try{
        const reply=JSON.parse(stdout.trim().split(/\r?\n/).at(-1));
        assert.equal(reply.id,41);
        resolve(reply.result);
      }catch(e){reject(e)}
    });
    cli.stdin.end(JSON.stringify({jsonrpc:'2.0',id:41,method,
      params:{port,request:body}})+'\n');
  });
}
const open=(key,audience,title)=>({idempotencyKey:key,audience,title});
const change=(key,audience,session,version,title)=>({
  idempotencyKey:key,audience,sessionId:session,expectedVersion:version,
  operation:'set-title',value:title
});
try{
  await launch(true);
  const health=await request('/v1/health');
  assert.equal(health.payload.synthetic_scoped_write_enforcement,true);
  assert.equal(health.payload.common_bearer_grants_mutation,false);
  assert.equal(health.payload.authenticated_os_peer_identity,false);
  assert.equal(health.payload.production_durable_admission,false);
  assert.equal((await request('/v1/sessions')).status,200);
  record('transport-bearer-read-only-synthetic-admission');

  const humanCreate=grant('human-one','human','create-only',['session.create']);
  const automationCreate=grant('auto-one','automation','create-only',['session.create']);
  const aOpen=open('human-open-00000001','human','A');
  assert.equal((await request('/v1/sessions/open',aOpen)).status,403);
  assert.equal((await request('/v1/sessions/open',aOpen,'bad-token')).status,403);
  assert.equal((await request('/v1/sessions/open',aOpen,
    grant('agent-one','agent','create-only',['session.mutate']))).status,403);
  const a=await request('/v1/sessions/open',aOpen,humanCreate);
  assert.equal(a.status,200);
  assert.equal(a.payload.applied,true);
  const sidA=a.payload.session.id;
  const bOpen=open('auto-open-00000001','automation','B');
  const b=await machine('session/ledger/open',bOpen,automationCreate);
  assert.equal(b.http_status,200);
  const sidB=b.payload.session.id;
  assert.notEqual(sidA,sidB);
  record('human-and-automation-create-with-signed-grants-no-common-bearer-bypass');

  const agentA=grant('agent-one','agent',sidA,['session.mutate']);
  const agentB=grant('agent-two','agent',sidA,['session.mutate']);
  const autoB=grant('auto-one','automation',sidB,['session.mutate']);
  const humanA=grant('human-one','human',sidA,['session.mutate']);
  const agentMut=change('agent-action-0000001','agent',sidA,0,'A-agent');
  assert.equal((await request('/v1/sessions/change',agentMut)).status,403);
  assert.equal((await request('/v1/sessions/change',agentMut,
    grant('agent-one','agent',sidB,['session.mutate']))).status,403);
  assert.equal((await request('/v1/sessions/change',agentMut,
    grant('agent-one','agent',sidA,['session.read']))).status,403);
  assert.equal((await request('/v1/sessions/change',
    {...agentMut,audience:'human'},agentA)).status,403);
  assert.equal((await request('/v1/sessions/change',agentMut,
    grant('agent-one','agent',sidA,['session.mutate'],{issued:Date.now()/1000-300,
      ttl:10}))).status,403);
  record('wrong-session-role-scope-expired-and-unsigned-write-denials');

  const humanAction=change('human-change-0000001','human',sidA,0,'A-human');
  const [agentRes,humanRes]=await Promise.all([
    machine('session/ledger/change',agentMut,agentA),
    request('/v1/sessions/change',humanAction,humanA)
  ]);
  const results=[{status:agentRes.http_status,payload:agentRes.payload},humanRes];
  assert.equal(results.filter(x=>x.status===200&&x.payload.applied).length,1);
  assert.equal(results.filter(x=>x.status===409&&
    x.payload.status==='VERSION_CONFLICT').length,1);
  const winner=agentRes.http_status===200
    ? {action:agentMut,cap:agentA}:{action:humanAction,cap:humanA};
  const loser=agentRes.http_status===409
    ? {action:agentMut,cap:agentA}:{action:humanAction,cap:humanA};
  const bChange=change('auto-change-0000001','automation',sidB,0,'B-progress');
  assert.equal((await machine('session/ledger/change',bChange,autoB)).http_status,200);
  const thirdActor=change('agent-action-0000001','agent',sidA,0,'third-actor');
  const third=await request('/v1/sessions/change',thirdActor,agentB);
  assert.equal(third.status,409);
  assert.equal(third.payload.status,'VERSION_CONFLICT');
  assert.equal(third.payload.isReplay,false);
  record('independent-agents-same-key-not-cross-replayed-plus-A-CAS-B-progress');

  await stop();await launch(false);
  const inventory=(await request('/v1/sessions')).payload;
  assert.equal(inventory.sessions.length,2);
  const replay=await request('/v1/sessions/change',winner.action,
    grant(winner.action.audience==='agent'?'agent-one':'human-one',
      winner.action.audience,sidA,['session.mutate']));
  assert.equal(replay.status,200);
  assert.equal(replay.payload.isReplay,true);
  const failAgain=await request('/v1/sessions/change',loser.action,loser.cap);
  assert.equal(failAgain.status,409);
  assert.equal(failAgain.payload.isReplay,true);
  assert.equal((await request('/v1/sessions/change',agentMut)).status,403);
  assert.equal((await request('/v1/sessions/change',bChange,autoB)).payload.isReplay,true);
  record('restart-with-new-grant-retains-authoritative-actor-scoped-first-outcomes');

  const view=await machine('session/ledger/view',undefined,undefined);
  assert.equal(view.connected,true);
  assert.deepEqual(new Set(view.sessions.map(s=>s.id)),new Set([sidA,sidB]));
  assert.equal(view.actor_identity_authenticated,false);
  record('same-accessible-reader-sees-two-shared-sessions-after-restart');

  const crossActorRevoke = await machine('session/ledger/revoke-own',
    {sessionId:sidA,audience:'human'},agentA);
  assert.equal(crossActorRevoke.http_status,403);
  const revoke = await machine('session/ledger/revoke-own',
    {sessionId:sidA,audience:'agent'},agentA);
  assert.equal(revoke.http_status,200);
  assert.equal(revoke.payload.status,'REVOKED');
  assert.equal(revoke.payload.durable_in_existing_w11_checkpoint,true);
  assert.equal(revoke.payload.owner_cross_actor_revocation_available,false);
  assert.equal((await request('/v1/sessions/change',agentMut,agentA)).status,403);
  const sameRevoke = await machine('session/ledger/revoke-own',
    {sessionId:sidA,audience:'agent'},agentA);
  assert.equal(sameRevoke.http_status,200);
  assert.equal(sameRevoke.payload.status,'ALREADY_REVOKED');
  const renewed = grant('agent-one','agent',sidA,['session.mutate']);
  const acceptedOrRejected = await request('/v1/sessions/change',agentMut,renewed);
  assert([200,409].includes(acceptedOrRejected.status));
  assert.equal(acceptedOrRejected.payload.isReplay,true);
  record('signed-self-revocation-and-independent-new-grant-replay');

  const raceGrant=grant('agent-racer','agent',sidB,['session.mutate']);
  const raceMutation=change('w13-race-action-00001','agent',sidB,1,'B-raced');
  const [raceResult,revocationResult]=await Promise.all([
    request('/v1/sessions/change',raceMutation,raceGrant),
    request('/v1/grants/revoke-self',{sessionId:sidB,audience:'agent'},raceGrant)
  ]);
  assert.equal(revocationResult.status,200);
  assert.equal(revocationResult.payload.status,'REVOKED');
  assert([200,403].includes(raceResult.status));
  assert.equal((await request('/v1/sessions/change',raceMutation,raceGrant)).status,403);
  record('parallel-revocation-versus-mutation-serialized-by-one-ledger-gate');

  await stop();await launch(false);
  assert.equal((await request('/v1/sessions/change',agentMut,agentA)).status,403);
  assert.equal((await request('/v1/sessions/change',raceMutation,raceGrant)).status,403);
  const replayAfterRestart=await request('/v1/sessions/change',agentMut,
    grant('agent-one','agent',sidA,['session.mutate']));
  assert([200,409].includes(replayAfterRestart.status));
  assert.equal(replayAfterRestart.payload.isReplay,true);
  const recoveredView=await machine('session/ledger/view',undefined,undefined);
  assert.equal(recoveredView.connected,true);
  assert.deepEqual(new Set(recoveredView.sessions.map(s=>s.id)),new Set([sidA,sidB]));
  const recoveredLedger=(await request('/v1/sessions')).payload;
  assert(recoveredLedger.events.some(e=>e.kind==='revoke-own-grant' &&
    e.sessionId===sidA));
  record('durable-revocations-survive-restart-and-same-actor-can-retry-with-new-grant');

  console.log(JSON.stringify({
    schema:'suprachat-w12-host-scoped-write-rdte/v1',
    result:'PASS',stages,independent_sessions:2,
    signed_actor_grants_enforced:true,
    common_bearer_write_bypass_denied:true,
    same_operation_key_actor_isolated:true,
    restart_receipts_bound_to_signed_actor:true,
    os_peer_authenticated:false,
    trusted_issuer_enrolled:false,
    durable_self_revocation_available:true,
    durable_cross_actor_revocation_available:false,
    real_provider_calls:0,terminal_commands:0
  }));
}finally{
  await stop();
  signingKey.fill(0);
  await rm(dir,{recursive:true,force:true,maxRetries:6,retryDelay:200});
}
