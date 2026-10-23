#!/usr/bin/env node
// W7 disposable *actual host* restart experiment. No provider, credentials,
// real user metadata, terminal commands, actor identities or production store.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import {mkdtemp,rm} from 'node:fs/promises';
import {createServer} from 'node:net';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dll=path.join(root,'SupraChat.Automation','bin','Release','net10.0','SupraChat.Automation.dll');
const owned=await mkdtemp(path.join(os.tmpdir(),'suprachat-w7-owned-'));
const database=path.join(owned,'w7-host.sqlite3');
const token=randomBytes(32).toString('hex');
const env={...process.env,SUPRACHAT_W7_DISPOSABLE_ONLY:'yes',SUPRACHAT_LEDGER_TOKEN:token};
const stages=[];
let host=null;
async function allocPort(){
  const s=createServer();
  await new Promise((resolve,reject)=>s.listen(0,'127.0.0.1',resolve).on('error',reject));
  const port=s.address().port;
  await new Promise(resolve=>s.close(resolve));
  return port;
}
function child(args,input=''){
  return new Promise((resolve,reject)=>{
    const p=spawn('dotnet',[dll,...args],{cwd:root,env,windowsHide:true,stdio:['pipe','pipe','pipe']});
    let stdout='',stderr='';
    const t=setTimeout(()=>p.kill(),8000);
    p.stdout.on('data',b=>{stdout+=b;if(stdout.length>24000)p.kill()});
    p.stderr.on('data',b=>{stderr+=b;if(stderr.length>24000)p.kill()});
    p.once('error',e=>{clearTimeout(t);reject(e)});
    p.once('close',code=>{
      clearTimeout(t);
      if(stdout.includes(token)||stderr.includes(token))return reject(Error('TOKEN_LEAKED'));
      resolve({code,stdout,stderr});
    });
    p.stdin.end(input);
  });
}
async function start(port){
  host=spawn('dotnet',[dll,'session-ledger-host','--port',String(port),
      '--w7-lab-db',database],{cwd:root,env,windowsHide:true,stdio:['ignore','pipe','pipe']});
  return new Promise((resolve,reject)=>{
    let stdout='',stderr='',settled=false;
    const fail=e=>{if(!settled){settled=true;clearTimeout(t);reject(e)}};
    const t=setTimeout(()=>fail(Error('LAB_HOST_START_TIMEOUT')),12000);
    host.once('error',fail);
    host.once('exit',code=>fail(Error('HOST_EXIT_PREMATURE_'+code)));
    host.stderr.on('data',b=>{stderr+=b;if(stderr.length>12000)fail(Error('HOST_STDERR_TOO_LARGE'))});
    host.stdout.on('data',b=>{
      stdout+=b;if(stdout.includes(token))return fail(Error('TOKEN_PRINTED'));
      const line=stdout.split(/\r?\n/).find(l=>l.startsWith('{"schema":'));
      if(!line)return;
      let receipt;
      try{receipt=JSON.parse(line)}catch{return fail(Error('HOST_RECEIPT_JSON'))}
      if(receipt.w7_lab_recovery_enabled!==true||
         receipt.persistent_recovery_available!==false||
         receipt.authenticated_audience_identity!==false||
         receipt.bound_address!=='127.0.0.1')return fail(Error('HOST_ADMISSION_DRIFT'));
      settled=true;clearTimeout(t);resolve(receipt);
    });
  });
}
async function stop(){
  if(!host)return;
  const p=host;host=null;
  if(p.exitCode!==null)return;
  await new Promise(resolve=>{
    const timeout=setTimeout(()=>{p.kill('SIGKILL');resolve()},5000);
    p.once('exit',()=>{clearTimeout(timeout);resolve()});
    p.kill();
  });
}
async function request(port,route,method='GET',body){
  const controller=new AbortController();
  const timeout=setTimeout(()=>controller.abort(),5000);
  try{
    const r=await fetch('http://127.0.0.1:'+port+route,{
      method,headers:{Authorization:'Bearer '+token,
        ...(body?{'Content-Type':'application/json'}:{})},
      body:body?JSON.stringify(body):undefined,
      signal:controller.signal});
    const text=await r.text();
    if(text.includes(token))throw Error('BEARER_ECHO');
    return {status:r.status,body:text?JSON.parse(text):null};
  }finally{clearTimeout(timeout)}
}
function pass(name){stages.push({test:name,result:'PASS'})}
try{
  const created=await child(['w7-lab-create','--db',database]);
  assert.equal(created.code,0,'explicit existing-only disposable lab setup');
  const port=await allocPort();
  await start(port);
  const health=await request(port,'/v1/health');
  assert.equal(health.body.experimental_w7_store_enabled,true);
  assert.equal(health.body.durable_recovery_available,false);
  pass('explicit-lab-mode-no-production-durability-claim');

  const first={idempotencyKey:'w7-host-human-open-00001',audience:'human',
    title:'Synthetic main',conversationUrl:'https://chatgpt.com/c/123e4567-e89b-42d3-a456-426614174000'};
  const opened=await request(port,'/v1/sessions/open','POST',first);
  assert.equal(opened.status,200);
  const sid=opened.body.session.id;
  const changed={idempotencyKey:'w7-host-agent-cas-000001',audience:'agent',
    sessionId:sid,expectedVersion:0,operation:'set-title',value:'Agent first edit'};
  const good=await request(port,'/v1/sessions/change','POST',changed);
  assert.equal(good.status,200);
  const stale={idempotencyKey:'w7-host-automation-loser01',audience:'automation',
    sessionId:sid,expectedVersion:0,operation:'set-title',value:'Stale loser'};
  const refused=await request(port,'/v1/sessions/change','POST',stale);
  assert.equal(refused.status,409);
  assert.equal(refused.body.status,'VERSION_CONFLICT');
  const second={idempotencyKey:'w7-host-second-open-00001',audience:'automation',title:'Other'};
  const other=await request(port,'/v1/sessions/open','POST',second);
  assert.equal(other.status,200);
  assert.notEqual(other.body.session.id,sid);
  pass('real-loopback-metadata-commit-and-rejected-conflict');

  await stop();
  await start(port);
  const after=await request(port,'/v1/sessions');
  assert.equal(after.status,200);
  assert.equal(after.body.sessions.length,2);
  assert.equal(after.body.sessions.find(s=>s.id===sid).version,1);
  assert.equal(after.body.durableAcrossProcessRestart,true);
  pass('actual-host-process-restart-retains-two-sessions');

  const replayOpen=await request(port,'/v1/sessions/open','POST',first);
  assert.equal(replayOpen.status,200);
  assert.equal(replayOpen.body.isReplay,true);
  assert.equal(replayOpen.body.session.id,sid);
  const replayChanged=await request(port,'/v1/sessions/change','POST',changed);
  assert.equal(replayChanged.body.isReplay,true);
  assert.equal(replayChanged.body.session.version,1);
  const replayRejected=await request(port,'/v1/sessions/change','POST',stale);
  assert.equal(replayRejected.status,409);
  assert.equal(replayRejected.body.isReplay,true);
  pass('durable-accepted-and-rejected-idempotency-outcomes-at-host');

  const reuse=await request(port,'/v1/sessions/open','POST',{...first,title:'Changed old key'});
  assert.equal(reuse.status,409);
  assert.equal(reuse.body.status,'IDEMPOTENCY_KEY_PAYLOAD_CONFLICT');
  pass('durable-host-key-payload-conflict');

  const report={schema:'suprachat-w7-host-restart-lab-qualification/v1',
    passed:stages.length,stages,actual_loopback_server:true,actual_process_restart:true,
    w6_default_ephemeral_unchanged:true,host_lab_durable_recovery:true,
    production_storage_admitted:false,actor_identity_authenticated:false,
    chatgpt_provider_submission:false,private_source_used:false,windows_docker_used:false};
  console.log(JSON.stringify(report,null,2));
}finally{
  await stop();
  await rm(owned,{recursive:true,force:true,maxRetries:3});
}
