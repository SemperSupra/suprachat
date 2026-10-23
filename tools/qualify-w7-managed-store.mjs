#!/usr/bin/env node
// W7 actual managed .NET SQLite storage experiment. All data synthetic,
// owned disposable paths. No live W6 host, actor auth or provider actions.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtemp,rm,open as openFile} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dll=path.join(root,'SupraChat.Automation','bin','Release','net10.0','SupraChat.Automation.dll');
const owned=await mkdtemp(path.join(os.tmpdir(),'suprachat-w7-owned-'));
const db=path.join(owned,'synthetic.sqlite3');
const stages=[];
function record(name){stages.push({name,result:'PASS'})}
async function execute(kind,payload,point){
  const env={...process.env,SUPRACHAT_W7_DISPOSABLE_ONLY:'yes'};
  if(point) env.SUPRACHAT_W7_FAULT_POINT=point;
  const args=[dll,'w7-lab-'+kind,'--db',db];
  const proc=spawn('dotnet',args,{cwd:root,env,windowsHide:true,stdio:['pipe','pipe','pipe']});
  let stdout='',stderr='';
  const timer=setTimeout(()=>proc.kill(),7000);
  return await new Promise((resolve,reject)=>{
    proc.stdout.on('data',b=>{stdout+=b; if(stdout.length>65536)proc.kill()});
    proc.stderr.on('data',b=>{stderr+=b; if(stderr.length>4000)proc.kill()});
    proc.once('error',e=>{clearTimeout(timer);reject(e)});
    proc.once('close',code=>{
      clearTimeout(timer);
      if(stderr.includes('SYNTHETIC_SECRET_TOKEN'))return reject(Error('PRIVATE_VALUE_IN_STDERR'));
      let value=null;
      try{value=stdout.trim()?JSON.parse(stdout):null}catch{}
      resolve({code,value,stderr:stderr.slice(0,200)});
    });
    proc.stdin.end(payload===undefined?'':JSON.stringify(payload));
  });
}
function success(result){
  assert.equal(result.code,0,'dotnet CLI must pass; stderr: '+result.stderr);
  assert.equal(result.value?.ok,true,'machine wrapper must report ok');
  return result.value.value;
}
try{
  success(await execute('create'));
  record('explicit-owned-research-only-store');
  const first={key:'w7-managed-human-open-00001',actor:'human',title:'Synthetic first'};
  let opened=success(await execute('open',first));
  assert.equal(opened.applied,true);
  const id=opened.session.id;
  const replay=success(await execute('open',first));
  assert.equal(replay.is_replay,true);
  assert.equal(replay.session.id,id);
  record('durable-open-idempotent-restart-replay');
  const original=success(await execute('inspect'));
  assert.equal(original.sessions.length,1);
  assert.equal(original.durable_across_process_restart,true);
  assert.equal(original.authenticated_audiences,false);
  record('durable-inspection-does-not-claim-actor-identity');
  const preCommit={key:'w7-managed-before-commit-001',actor:'agent',sessionId:id,
    expectedVersion:0,operation:'set-title',value:'Uncommitted mutation'};
  let crashed=await execute('change',preCommit,'after_write_before_commit');
  assert.equal(crashed.code,83);
  assert.equal(success(await execute('inspect')).sessions[0].version,0);
  const recovered=success(await execute('change',preCommit));
  assert.equal(recovered.applied,true);
  assert.equal(recovered.is_replay,false);
  assert.equal(recovered.session.version,1);
  record('real-process-crash-before-commit-rolled-back');
  const lostAck={key:'w7-managed-after-ack-00001',actor:'automation',sessionId:id,
    expectedVersion:1,operation:'set-title',value:'Committed but ACK lost'};
  crashed=await execute('change',lostAck,'after_commit_before_ack');
  assert.equal(crashed.code,84);
  assert.equal(success(await execute('inspect')).sessions[0].version,2);
  const recoveredAck=success(await execute('change',lostAck));
  assert.equal(recoveredAck.is_replay,true);
  assert.equal(recoveredAck.session.version,2);
  record('real-process-crash-after-commit-replays-without-duplicate');
  const stale={key:'w7-managed-rejected-cas-0001',actor:'agent',sessionId:id,
    expectedVersion:0,operation:'set-title',value:'Do not overwrite'};
  const refused=success(await execute('change',stale));
  assert.equal(refused.applied,false);
  assert.equal(refused.status,'VERSION_CONFLICT');
  const repeated=success(await execute('change',stale));
  assert.equal(repeated.is_replay,true);
  assert.equal(repeated.status,'VERSION_CONFLICT');
  record('durable-negative-first-outcome-replay');
  const collision=success(await execute('change',{...stale,value:'Changed-key-payload'}));
  assert.equal(collision.status,'IDEMPOTENCY_KEY_PAYLOAD_CONFLICT');
  record('key-reused-with-changed-payload-refused');
  const next=success(await execute('open',
    {key:'w7-managed-second-open-00001',actor:'automation',title:'Second independent'}));
  assert.notEqual(next.session.id,id);
  assert.equal(success(await execute('inspect')).sessions.length,2);
  record('two-sessions-survive-independent-process-restart');
  // The database is entirely disposable and every writer has exited. An
  // invalid SQLite header must refuse restoration, not create an empty
  // authority with lost idempotency history.
  const corruptHandle=await openFile(db,'r+');
  try{
    await corruptHandle.write(Buffer.from('NOTSQLITE'),0,9,0);
    await corruptHandle.sync();
  }finally{await corruptHandle.close();}
  const corruptResult=await execute('inspect');
  assert.notEqual(corruptResult.code,0,'Corrupt database was silently reinitialized');
  record('corrupted-owned-database-fails-closed');
  const report={schema:'suprachat-w7-managed-sqlite-qualification/v1',
    passed:stages.length,stages,actual_process_restart:true,
    admission:'RESEARCH_ONLY_NOT_W6_INTEGRATED',
    private_source_used:false,windows_docker_used:false,
    chatgpt_prompt_sent:false,production_durable_host_available:false};
  console.log(JSON.stringify(report,null,2));
}finally{
  await rm(owned,{recursive:true,force:true,maxRetries:3});
}
