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
const envBase={...process.env,SUPRACHAT_LEDGER_TOKEN:token,SUPRACHAT_LEDGER_STORE:store};
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
  assert.equal(inventory.durableAcrossProcessRestart,true);
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
    durable_provider_prompt_receipts:false,
    authenticated_audience_identity:false,
    chatgpt_prompt_sent:false,terminal_command_executed:false,
    windows_docker_installed:false,private_source_used:false
  }));
}finally{
  await stop();
  await rm(temp,{recursive:true,force:true,maxRetries:8,retryDelay:200});
}
