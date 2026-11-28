#!/usr/bin/env node
// W9: actual W6 HTTP host + existing agent JSON-RPC across kill/restart.
// Disposable synthetic metadata only; no ChatGPT provider or terminal calls.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dll=path.join(root,'SupraChat.Automation','bin','Release','net10.0','SupraChat.Automation.dll');
const token=randomBytes(32).toString('base64url');
const temp=await mkdtemp(path.join(os.tmpdir(),'suprachat-w9-'));
const store=path.join(temp,'w9.sqlite');
const envBase={...process.env,SUPRACHAT_W6_DISPOSABLE_ONLY:'yes',
  SUPRACHAT_LEDGER_TOKEN:token,SUPRACHAT_LEDGER_STORE:store};
const stages=[];
let host=null,port;
const record=(name)=>stages.push({name,status:'PASS'});

async function unusedPort(){
  return new Promise((resolve,reject)=>{
    const s=createServer();s.once('error',reject);
    s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>resolve(p));});
  });
}
function spawnHost(p,env=envBase){
  return spawn('dotnet',[dll,'session-ledger-host','--port',String(p)],{
    cwd:root,env,stdio:['ignore','pipe','pipe'],windowsHide:true});
}
async function launch(env=envBase){
  port=await unusedPort();host=spawnHost(port,env);
  let output='',stderr='';
  return await new Promise((resolve,reject)=>{
    let complete=false;
    const t=setTimeout(()=>fail(new Error('HOST_START_TIMEOUT')),15000);
    function fail(e){if(!complete){complete=true;clearTimeout(t);reject(e)}}
    host.once('error',fail);
    host.once('exit',code=>fail(new Error('HOST_EXIT_PREMATURE_'+code)));
    host.stderr.on('data',b=>{stderr+=b;if(stderr.length>8000)fail(new Error('HOST_STDERR_OVERFLOW'))});
    host.stdout.on('data',b=>{
      output+=b;
      if(output.includes(token))return fail(new Error('BEARER_IN_OUTPUT'));
      const line=output.split(/\r?\n/).find(s=>s.startsWith('{"schema":'));
      if(!line)return;
      let receipt;try{receipt=JSON.parse(line)}catch{return fail(new Error('BAD_READY_RECEIPT'))}
      if(receipt.port!==port || receipt.bound_address!=='127.0.0.1' ||
         receipt.persistent_recovery_available!==true ||
         receipt.authenticated_audience_identity!==false)
        return fail(new Error('UNSAFE_OR_MISSING_DURABILITY_DESCRIPTOR'));
      if(!complete){complete=true;clearTimeout(t);resolve();}
    });
  });
}
async function waitExit(p){
  if(p.exitCode!==null)return p.exitCode;
  return await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>reject(new Error('HOST_FAULT_EXIT_TIMEOUT')),7000);
    p.once('exit',code=>{clearTimeout(timer);resolve(code)});
  });
}
async function stop(){
  const p=host;host=null;if(!p || p.exitCode!==null)return;
  await new Promise(resolve=>{
    const timeout=setTimeout(()=>{p.kill('SIGKILL');resolve()},7000);
    p.once('exit',()=>{clearTimeout(timeout);resolve()});
    p.kill();
  });
}
async function request(route,method='GET',payload,provided=token,origin=null){
  const headers={};
  if(provided!==null)headers.Authorization='Bearer '+provided;
  if(origin)headers.Origin=origin;
  if(payload!==undefined)headers['Content-Type']='application/json';
  const controller=new AbortController();
  const t=setTimeout(()=>controller.abort(),8000);
  try{
    const res=await fetch('http://127.0.0.1:'+port+route,{
      method,headers,signal:controller.signal,
      body:payload===undefined?undefined:JSON.stringify(payload)});
    const body=await res.text();assert(body.length<=128000);
    assert(!body.includes(token),'BEARER_LEAK');
    return {status:res.status,body:body?JSON.parse(body):null};
  }finally{clearTimeout(t)}
}
async function agentRpc(method,payload){
  const req={jsonrpc:'2.0',id:7,method,
    params:{port,request:payload}};
  return new Promise((resolve,reject)=>{
    const child=spawn('dotnet',[dll,'stdio'],{cwd:root,env:envBase,
      stdio:['pipe','pipe','pipe'],windowsHide:true});
    let stdout='',stderr='';
    const timer=setTimeout(()=>{child.kill();reject(new Error('AGENT_TIMEOUT'))},20000);
    child.stdout.on('data',b=>{stdout+=b;if(stdout.length>160000)child.kill()});
    child.stderr.on('data',b=>{stderr+=b;if(stderr.length>8000)child.kill()});
    child.on('error',e=>{clearTimeout(timer);reject(e)});
    child.on('close',code=>{
      clearTimeout(timer);
      if(code!==0)return reject(new Error('AGENT_EXIT_'+code));
      try{
        const msg=JSON.parse(stdout.trim().split(/\r?\n/).at(-1));
        assert.equal(msg.id,7);assert(msg.result);
        resolve(msg.result);
      }catch(e){reject(e)}
    });
    child.stdin.end(JSON.stringify(req)+'\n');
  });
}
const open=(key,audience,title)=>({idempotencyKey:key,audience,title});
const change=(key,audience,id,ver,title)=>({
  idempotencyKey:key,audience,sessionId:id,expectedVersion:ver,
  operation:'set-title',value:title});
try{
  await launch();
  const health=await request('/v1/health');
  assert.equal(health.status,200);
  assert.equal(health.body.durable_recovery_available,true);
  assert.equal(health.body.audience_identity_authenticated,false);
  assert.equal((await request('/v1/sessions','GET',undefined,null)).status,401);
  assert.equal((await request('/v1/sessions','GET',undefined,'wrong')).status,401);
  assert.equal((await request('/v1/sessions','GET',undefined,token,'https://attacker.invalid')).status,403);
  record('local-auth-origin-and-explicit-durable-admission');

  const aReq=open('w9-human-session-open-000001','human','synthetic-A');
  const a=await request('/v1/sessions/open','POST',aReq);
  assert.equal(a.status,200);
  assert.equal(a.body.applied,true);
  const sidA=a.body.session.id;
  const bReq=open('w9-agent-session-open-000002','agent','synthetic-B');
  const b=await agentRpc('session/ledger/open',bReq);
  assert.equal(b.http_status,200);
  const sidB=b.payload.session.id;
  assert.notEqual(sidA,sidB);
  record('two-sessions-human-and-agent-open');

  const autoAction=change('w9-auto-change-000000001','automation',sidA,0,'A-auto');
  const agentAction=change('w9-agent-change-00000001','agent',sidA,0,'A-agent');
  const [auto,agent]=await Promise.all([
    request('/v1/sessions/change','POST',autoAction),
    agentRpc('session/ledger/change',agentAction)
  ]);
  const decisions=[{status:auto.status,result:auto.body},
    {status:agent.http_status,result:agent.payload}];
  assert.equal(decisions.filter(x=>x.status===200&&x.result.applied).length,1);
  assert.equal(decisions.filter(x=>x.status===409&&x.result.status==='VERSION_CONFLICT').length,1);
  const losing=auto.status===409?autoAction:agentAction;
  const winning=auto.status===200?autoAction:agentAction;
  const rejected=await request('/v1/sessions/change','POST',losing);
  assert.equal(rejected.status,409);
  assert.equal(rejected.body.isReplay,true);
  assert.equal((await request('/v1/sessions')).body.sessions.length,2);
  const bUpdate=change('w9-b-independent-0000001','automation',sidB,0,'B-continues');
  assert.equal((await request('/v1/sessions/change','POST',bUpdate)).status,200);
  record('multi-process-cas-and-independent-B-progress');

  const contender=spawnHost(await unusedPort());
  const contestCode=await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{contender.kill();reject(new Error('CONTENDER_NOT_DENIED'))},7000);
    contender.on('error',e=>{clearTimeout(timer);reject(e)});
    contender.on('exit',code=>{clearTimeout(timer);resolve(code)});
  });
  assert.notEqual(contestCode,0);
  record('exclusive-single-writer-W6-host');

  await stop();
  await launch();
  let inventory=(await request('/v1/sessions')).body;
  // The *pure W6 ledger inventory* intentionally advertises no inherent
  // process durability. The separately admitted W9 host owns that guarantee
  // through /v1/health and externally observed SQLite restart/replay.
  assert.equal(inventory.durableAcrossProcessRestart,false);
  const healthAfterRestart=await request('/v1/health');
  assert.equal(healthAfterRestart.status,200);
  assert.equal(healthAfterRestart.body.durable_recovery_available,true);
  assert.equal(inventory.authenticatedAudiences,false);
  assert.equal(inventory.sessions.length,2);
  assert.equal(inventory.sessions.find(s=>s.id===sidA).version,1);
  assert.equal(inventory.sessions.find(s=>s.id===sidB).version,1);
  const acceptedAgain=await request('/v1/sessions/change','POST',winning);
  assert.equal(acceptedAgain.body.isReplay,true);
  const rejectedAgain=await request('/v1/sessions/change','POST',losing);
  assert.equal(rejectedAgain.status,409);assert.equal(rejectedAgain.body.isReplay,true);
  const originalB=await agentRpc('session/ledger/change',bUpdate);
  assert.equal(originalB.payload.isReplay,true);
  record('restart-recovers-both-sessions-applied-and-rejected-receipts');

  await stop();
  const uncommitted=open('w9-crash-before-commit-0001','human','temporary');
  await launch({...envBase,SUPRACHAT_W9_RDTE_FAULTS:'1',SUPRACHAT_W9_FAULT:'BEFORE_COMMIT'});
  await Promise.allSettled([request('/v1/sessions/open','POST',uncommitted)]);
  const codeBefore=await waitExit(host);
  assert.equal(codeBefore,88);
  host=null;
  await launch();
  inventory=(await request('/v1/sessions')).body;
  assert.equal(inventory.sessions.length,2);
  const later=await request('/v1/sessions/open','POST',uncommitted);
  assert.equal(later.status,200);
  assert.equal(later.body.isReplay,false);
  record('crash-before-commit-produces-no-side-effect-or-receipt');

  await stop();
  const committed=change('w9-crash-after-commit-0001','agent',sidB,1,'B-committed');
  await launch({...envBase,SUPRACHAT_W9_RDTE_FAULTS:'1',SUPRACHAT_W9_FAULT:'AFTER_COMMIT_BEFORE_ACK'});
  await Promise.allSettled([request('/v1/sessions/change','POST',committed)]);
  const codeAfter=await waitExit(host);
  assert.equal(codeAfter,89);
  host=null;
  await launch();
  const after=await request('/v1/sessions/change','POST',committed);
  assert.equal(after.status,200);
  assert.equal(after.body.isReplay,true);
  assert.equal(after.body.session.version,2);
  record('lost-ack-replays-durable-first-outcome-not-second-change');

  // R50: three audiences concurrently contest ONE session's immutable
  // version, through HTTP and a separate agent JSON-RPC process. This
  // explicitly does NOT prove independently authenticated human identity
  // or higher-priority human preemption; W8/W9 epoch integration is HOLD.
  const beforeRace=(await request('/v1/sessions')).body;
  const aBefore=beforeRace.sessions.find(s=>s.id===sidA);
  const bBefore=beforeRace.sessions.find(s=>s.id===sidB);
  assert.equal(aBefore.version,1);
  assert.equal(bBefore.version,2);
  const replayContestant=async (c)=>{
    if(c.kind==='agent'){
      const r=await agentRpc('session/ledger/change',c.req);
      return {status:r.http_status,body:r.payload};
    }
    return request('/v1/sessions/change','POST',c.req);
  };
  const verifyReplay=(first,replay)=>{
    assert.equal(replay.status,first.status);
    assert.deepEqual(replay.body,{...first.body,isReplay:true});
  };
  const verifyUnchanged=(observed,expected)=>{
    assert.deepEqual(observed,expected,'UNEXPECTED_LEDGER_MUTATION');
  };
  const humanRace=change('w50-human-same-session-0001','human',
                         sidA,1,'synthetic-human-race');
  const autoRace=change('w50-auto-same-session-00001','automation',
                        sidA,1,'synthetic-automation-race');
  const agentRace=change('w50-agent-same-session-0001','agent',
                         sidA,1,'synthetic-agent-race');
  const [humanR,autoR,agentR]=await Promise.all([
    request('/v1/sessions/change','POST',humanRace),
    request('/v1/sessions/change','POST',autoRace),
    agentRpc('session/ledger/change',agentRace)
  ]);
  const contestants=[
    {kind:'human',status:humanR.status,body:humanR.body,req:humanRace},
    {kind:'automation',status:autoR.status,body:autoR.body,req:autoRace},
    {kind:'agent',status:agentR.http_status,body:agentR.payload,req:agentRace}
  ];
  assert.equal(contestants.filter(x=>x.status===200&&x.body.applied).length,1);
  assert.equal(contestants.filter(x=>x.status===409&&
                                    x.body.status==='VERSION_CONFLICT').length,2);
  const raceWinner=contestants.find(x=>x.status===200&&x.body.applied);
  assert.equal(raceWinner.body.session.id,sidA);
  assert.equal(raceWinner.body.session.title,raceWinner.req.value);
  assert.equal(raceWinner.body.session.version,2);
  const afterRace=(await request('/v1/sessions')).body;
  assert.deepEqual(afterRace.sessions.find(s=>s.id===sidA),raceWinner.body.session);
  assert.deepEqual(afterRace.sessions.find(s=>s.id===sidB),bBefore);
  assert.equal(afterRace.sequence,beforeRace.sequence+1);
  assert.deepEqual(afterRace.events.slice(beforeRace.events.length),[{
    sequence:afterRace.sequence,sessionId:sidA,audience:raceWinner.kind,
    operation:'set-title',sessionVersion:2
  }]);
  for(const c of contestants){
    assert.equal(c.body.isReplay,false);
    verifyReplay(c,await replayContestant(c));
  }
  verifyUnchanged((await request('/v1/sessions')).body,afterRace);
  record('three-audiences-same-session-single-writer-cas');
  // A later synthetic human request is accepted at the NEW revision, while
  // stale automation/agent proposals remain refused. This is NOT human
  // priority, real guidance capture, or physical-tool fencing.
  const humanFollow=change('w50-human-followup-00001','human',
                           sidA,2,'synthetic-human-updated-intent');
  const follow=await request('/v1/sessions/change','POST',humanFollow);
  assert.equal(follow.status,200);
  assert.equal(follow.body.applied,true);
  assert.equal(follow.body.session.version,3);
  assert.equal(follow.body.session.id,sidA);
  assert.equal(follow.body.session.title,humanFollow.value);
  const staleAuto=change('w50-stale-auto-00000001','automation',
                         sidA,2,'unadmitted-stale-work');
  const staleAgent=change('w50-stale-agent-0000001','agent',
                          sidA,2,'unadmitted-agent-work');
  const stale=await request('/v1/sessions/change','POST',staleAuto);
  const staleAgentR=await agentRpc('session/ledger/change',staleAgent);
  assert.equal(stale.status,409);
  assert.equal(stale.body.status,'VERSION_CONFLICT');
  assert.equal(staleAgentR.http_status,409);
  assert.equal(staleAgentR.payload.status,'VERSION_CONFLICT');
  const reuse=await request('/v1/sessions/change','POST',
    {...raceWinner.req,sessionId:sidB,expectedVersion:bBefore.version,value:'wrong-target'});
  assert.equal(reuse.status,409);
  assert.equal(reuse.body.status,'IDEMPOTENCY_KEY_PAYLOAD_CONFLICT');
  const afterFollow=(await request('/v1/sessions')).body;
  assert.deepEqual(afterFollow.sessions.find(s=>s.id===sidA),follow.body.session);
  assert.deepEqual(afterFollow.sessions.find(s=>s.id===sidB),bBefore);
  assert.equal(afterFollow.sequence,afterRace.sequence+1);
  record('revision-bound-human-followup-stale-actors-and-wrong-target-refused');

  await stop();
  await launch();
  inventory=(await request('/v1/sessions')).body;
  verifyUnchanged(inventory,afterFollow);
  // Replay all three original outcomes AFTER further mutation and restart.
  // A fresh current-state response is not a valid historical receipt.
  for(const c of contestants)verifyReplay(c,await replayContestant(c));
  verifyReplay(follow,await request('/v1/sessions/change','POST',humanFollow));
  verifyReplay(stale,await request('/v1/sessions/change','POST',staleAuto));
  const staleAgentReplay=await agentRpc('session/ledger/change',staleAgent);
  verifyReplay({status:staleAgentR.http_status,body:staleAgentR.payload},
               {status:staleAgentReplay.http_status,body:staleAgentReplay.payload});
  verifyUnchanged((await request('/v1/sessions')).body,afterFollow);
  assert.equal(inventory.authenticatedAudiences,false);
  record('three-audience-shared-session-exact-outcomes-recovered-after-restart');

  // Explicit target restoration: B is archived later, but restore A.
  // This is the metadata contract only, not native GUI restore qualification.
  const archiveA={...change('w54-archive-A-00000001','human',sidA,3,null),operation:'archive'};
  const archiveB={...change('w54-archive-B-00000001','automation',sidB,2,null),operation:'archive'};
  const arA=await request('/v1/sessions/change','POST',archiveA);
  const arB=await request('/v1/sessions/change','POST',archiveB);
  assert.equal(arA.status,200);assert.equal(arA.body.session.isArchived,true);
  assert.equal(arB.status,200);assert.equal(arB.body.session.isArchived,true);
  const restoreA={...change('w54-restore-A-00000001','agent',sidA,4,null),operation:'restore'};
  const restored=await agentRpc('session/ledger/change',restoreA);
  assert.equal(restored.http_status,200);
  assert.equal(restored.payload.session.id,sidA);
  assert.equal(restored.payload.session.isArchived,false);
  assert.equal(restored.payload.session.version,5);
  const restoredInventory=(await request('/v1/sessions')).body;
  assert.deepEqual(restoredInventory.sessions.find(s=>s.id===sidB),arB.body.session);
  assert.deepEqual(restoredInventory.sessions.find(s=>s.id===sidA),restored.payload.session);
  record('explicit-archive-restore-target-not-latest-archive');

  // R54: a historical APPLIED archive receipt is NOT proof that A is
  // currently archived. Exercise the real W9 host's crash-before-commit
  // and lost-ack-after-commit seams on the SAME immutable close request.
  // This tests metadata only; UI draft generation and selection are not
  // present in this process and must be qualified separately.
  const closeAfterRestore={...change('w54-close-lost-ack-000001','human',sidA,5,null),
                           operation:'archive'};
  await stop();
  await launch({...envBase,SUPRACHAT_W9_RDTE_FAULTS:'1',SUPRACHAT_W9_FAULT:'BEFORE_COMMIT'});
  const closeBefore=await Promise.allSettled([
    request('/v1/sessions/change','POST',closeAfterRestore)
  ]);
  assert(closeBefore[0].status==='rejected' || closeBefore[0].value.status!==200,
         'CLOSE_ACK_BEFORE_COMMIT');
  assert.equal(await waitExit(host),88);
  host=null;
  await launch();
  verifyUnchanged((await request('/v1/sessions')).body,restoredInventory);
  record('archive-crash-before-commit-preserves-active-target-and-inventory');

  await stop();
  await launch({...envBase,SUPRACHAT_W9_RDTE_FAULTS:'1',SUPRACHAT_W9_FAULT:'AFTER_COMMIT_BEFORE_ACK'});
  const closeLost=await Promise.allSettled([
    request('/v1/sessions/change','POST',closeAfterRestore)
  ]);
  assert(closeLost[0].status==='rejected' || closeLost[0].value.status!==200,
         'CLOSE_ACK_NOT_LOST');
  assert.equal(await waitExit(host),89);
  host=null;
  await launch();
  const archivedReadback=(await request('/v1/sessions')).body;
  const recoveredClose=await request('/v1/sessions/change','POST',closeAfterRestore);
  assert.equal(recoveredClose.status,200);
  assert.equal(recoveredClose.body.applied,true);
  assert.equal(recoveredClose.body.isReplay,true);
  assert.equal(recoveredClose.body.session.id,sidA);
  assert.equal(recoveredClose.body.session.version,6);
  assert.equal(recoveredClose.body.session.isArchived,true);
  assert.deepEqual(archivedReadback.sessions.find(s=>s.id===sidA),recoveredClose.body.session);
  assert.deepEqual(archivedReadback.sessions.find(s=>s.id===sidB),arB.body.session);
  assert.equal(archivedReadback.sequence,restoredInventory.sequence+1);
  assert.deepEqual(archivedReadback.events.slice(restoredInventory.events.length),[{
    sequence:archivedReadback.sequence,sessionId:sidA,audience:'human',
    operation:'archive',sessionVersion:6
  }]);
  verifyUnchanged((await request('/v1/sessions')).body,archivedReadback);
  record('archive-lost-ack-recovers-original-target-bound-commit');

  const laterRestore={...change('w54-later-restore-00001','agent',sidA,6,null),operation:'restore'};
  const laterRestored=await agentRpc('session/ledger/change',laterRestore);
  assert.equal(laterRestored.http_status,200);
  assert.equal(laterRestored.payload.session.id,sidA);
  assert.equal(laterRestored.payload.session.version,7);
  assert.equal(laterRestored.payload.session.isArchived,false);
  const currentAfterRestore=(await request('/v1/sessions')).body;
  assert.equal(currentAfterRestore.sequence,archivedReadback.sequence+1);
  assert.deepEqual(currentAfterRestore.sessions.find(s=>s.id===sidA),laterRestored.payload.session);
  assert.deepEqual(currentAfterRestore.sessions.find(s=>s.id===sidB),arB.body.session);
  const staleClose={...closeAfterRestore,idempotencyKey:'w54-fresh-stale-close-001'};
  const staleCloseResult=await request('/v1/sessions/change','POST',staleClose);
  assert.equal(staleCloseResult.status,409);
  assert.equal(staleCloseResult.body.status,'VERSION_CONFLICT');
  assert.equal(staleCloseResult.body.session.version,7);
  assert.equal(staleCloseResult.body.session.isArchived,false);
  const wrongTargetClose=await request('/v1/sessions/change','POST',
    {...closeAfterRestore,sessionId:sidB,expectedVersion:arB.body.session.version});
  assert.equal(wrongTargetClose.status,409);
  assert.equal(wrongTargetClose.body.status,'IDEMPOTENCY_KEY_PAYLOAD_CONFLICT');
  await stop();
  await launch();
  verifyUnchanged((await request('/v1/sessions')).body,currentAfterRestore);
  const oldArchive=await request('/v1/sessions/change','POST',closeAfterRestore);
  // The old APPLIED result is immutable and still describes version 6,
  // even though the current target is active at version 7.
  assert.deepEqual(oldArchive,recoveredClose);
  verifyReplay(staleCloseResult,await request('/v1/sessions/change','POST',staleClose));
  verifyUnchanged((await request('/v1/sessions')).body,currentAfterRestore);
  record('historical-archive-replay-does-not-rearchive-restored-current-target');



  await stop();
  // Corrupt metadata store in a separate disposable location, not the gold DB.
  const corruptPath=path.join(temp,'corrupt.sqlite');
  await writeFile(corruptPath,Buffer.alloc(1024,0x78));
  const error=spawnHost(await unusedPort(),
    {...envBase,SUPRACHAT_LEDGER_STORE:corruptPath});
  const badCode=await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{error.kill();reject(new Error('CORRUPT_HOST_ADMITTED'))},7000);
    error.once('exit',code=>{clearTimeout(timer);resolve(code)});
  });
  assert.notEqual(badCode,0);
  record('corrupted-store-fails-closed-before-listener-ready');

  console.log(JSON.stringify({
    schema:'suprachat-w9-integrated-w6-durability-rdte/v1',result:'PASS',
    stages, independent_sessions:2,
    independent_client_processes:true,
    recovered_applied_and_rejected_receipts:true,
    host_restart_and_lost_ack:true,
    concurrent_human_automation_agent_same_session:true,
    three_audience_cas_recovered_after_restart:true,
    independently_authenticated_human_priority:false,
    durable_preemption_epoch_enforced:false,
    durable_provider_prompt_receipts:false,
    authenticated_audience_identity:false,
    chatgpt_prompt_sent:false,terminal_command_executed:false,
    windows_docker_installed:false,private_source_used:false
  }));
}finally{
  await stop();
  await rm(temp,{recursive:true,force:true,maxRetries:8,retryDelay:200});
}
