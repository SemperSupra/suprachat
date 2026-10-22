#!/usr/bin/env node
// Public-source only: independent CLI, JSON-RPC and HTTP clients share one
// real authenticated loopback ledger host. No ChatGPT/terminal operation.
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {spawn} from 'node:child_process';
import {createServer} from 'node:net';
import path from 'node:path';

const project = path.resolve('SupraChat.Automation/SupraChat.Automation.csproj');
const token = randomBytes(32).toString('base64url');
const env = {...process.env, SUPRACHAT_LEDGER_TOKEN: token};
const target = 'https://chatgpt.com/c/123e4567-e89b-42d3-a456-426614174000';
const receipt = {
  schema: 'suprachat-w6-cross-process-qualification/v1',
  provider_access: false, terminal_command_execution: false,
  private_source_access: false, persistent_recovery: false,
  authenticated_audience_identity: false, container_engine_used: false,
  stages: [],
};
const program = (...args) => ['run', '--no-build', '-c', 'Release',
  '--project', project, '--', ...args];

async function freePort() {
  const server = createServer();
  await new Promise((ok,bad)=>server.once('error',bad).listen(0,'127.0.0.1',ok));
  const port=server.address().port;
  await new Promise(ok=>server.close(ok));
  return port;
}
async function cli(args, input='') {
  return new Promise((resolve,reject)=>{
    const child=spawn('dotnet',program(...args),{env,stdio:['pipe','pipe','pipe']});
    let out='',err='';
    const deadline=setTimeout(()=>child.kill(),20000);
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data',x=>{out+=x;if(out.length>128000)child.kill();});
    child.stderr.on('data',x=>{err+=x;if(err.length>16000)child.kill();});
    child.on('error',()=>{clearTimeout(deadline);reject(new Error('CLI_NOT_AVAILABLE'));});
    child.on('close',code=>{
      clearTimeout(deadline);
      if(code!==0)return reject(new Error('CLI_NONZERO_EXIT'));
      try{resolve(JSON.parse(out.trim()));}catch{reject(new Error('CLI_INVALID_JSON'));}
    });
    child.stdin.end(input);
  });
}
async function http(port,method,route,body,auth=token,origin=null) {
  const headers={authorization:'Bearer '+auth};
  if(body!==undefined)headers['content-type']='application/json';
  if(origin)headers.origin=origin;
  const res=await fetch('http://127.0.0.1:'+port+route,{
    method,headers,body:body===undefined?undefined:JSON.stringify(body),
    signal:AbortSignal.timeout(6000)
  });
  let payload=null;
  try{payload=await res.json();}catch{}
  return {status:res.status,payload};
}

const port=await freePort();
let host=null,hostExited=false;
try {
  host=spawn('dotnet',program('session-ledger-host','--port',String(port)),
    {env,stdio:['ignore','pipe','pipe']});
  host.once('exit',()=>{hostExited=true;});
  const ready=await new Promise((resolve,reject)=>{
    let output='',errors='';
    const deadline=setTimeout(()=>reject(new Error('HOST_READINESS_TIMEOUT')),20000);
    host.stdout.setEncoding('utf8');
    host.stderr.setEncoding('utf8');
    host.stdout.on('data',part=>{
      output+=part;
      if(output.length>8192)return reject(new Error('HOST_BANNER_TOO_LARGE'));
      if(!output.includes('\n'))return;
      clearTimeout(deadline);
      try{resolve(JSON.parse(output.split('\n')[0]));}
      catch{reject(new Error('HOST_BANNER_INVALID'));}
    });
    host.stderr.on('data',part=>{errors+=part;if(errors.length>8192)reject(new Error('HOST_STDERR_LIMIT'));});
    host.once('exit',()=>{clearTimeout(deadline);reject(new Error('HOST_EXIT_BEFORE_READY'));});
  });
  assert.equal(ready.schema,'suprachat-w6-loopback-session-host/v1');
  assert.equal(ready.bound_address,'127.0.0.1');
  assert.equal(ready.bearer_token_required,true);
  assert.equal(ready.authenticated_audience_identity,false);
  assert.equal(ready.persistent_recovery_available,false);
  receipt.stages.push({name:'explicit-shared-loopback-host',state:'PASS'});

  assert.equal((await http(port,'GET','/v1/sessions',undefined,'wrong')).status,401);
  assert.equal((await http(port,'GET','/v1/sessions',undefined,token,'https://attacker.invalid')).status,403);
  receipt.stages.push({name:'bearer-and-browser-origin-denial',state:'PASS'});

  const opened={idempotencyKey:'w6-human-open-0000001',audience:'human',
    title:'Shared session',conversationUrl:target};
  const human=await cli(['session-ledger-open','--port',String(port)],JSON.stringify(opened));
  assert.equal(human.ok,true);
  assert.equal(human.value.http_status,200);
  assert.equal(human.value.payload.applied,true);
  const sid=human.value.payload.session.id;
  const retry=await cli(['session-ledger-open','--port',String(port)],JSON.stringify(opened));
  assert.equal(retry.value.payload.isReplay,true);
  assert.equal(retry.value.payload.session.id,sid);
  receipt.stages.push({name:'human-cli-idempotent-create',state:'PASS'});

  const read=await cli(['stdio'],JSON.stringify({
    jsonrpc:'2.0',id:6,method:'session/ledger/list',params:{port}
  })+'\n');
  assert.equal(read.id,6);
  assert.equal(read.result.payload.sessions.length,1);
  assert.equal(read.result.payload.sessions[0].id,sid);
  receipt.stages.push({name:'agent-stdio-shared-read',state:'PASS'});

  const autoRequest={idempotencyKey:'w6-auto-cas-00000001',audience:'automation',
    sessionId:sid,expectedVersion:0,operation:'set-title',value:'Automation proposed'};
  const agentRequest={idempotencyKey:'w6-agent-cas-0000001',audience:'agent',
    sessionId:sid,expectedVersion:0,operation:'set-title',value:'Agent proposed'};
  const [auto,agent]=await Promise.all([
    http(port,'POST','/v1/sessions/change',autoRequest),
    cli(['stdio'],JSON.stringify({jsonrpc:'2.0',id:7,method:'session/ledger/change',
      params:{port,request:agentRequest}})+'\n')
  ]);
  const outcomes=[auto.payload,agent.result.payload];
  assert.equal(outcomes.filter(x=>x.applied).length,1);
  assert.equal(outcomes.filter(x=>x.status==='VERSION_CONFLICT').length,1);
  const loser=auto.payload.applied?agentRequest:autoRequest;
  const repeated=await http(port,'POST','/v1/sessions/change',loser);
  assert.equal(repeated.status,409);
  assert.equal(repeated.payload.isReplay,true);
  assert.equal(repeated.payload.status,'VERSION_CONFLICT');
  receipt.stages.push({name:'cross-process-human-agent-automation-cas',state:'PASS'});

  const second=await http(port,'POST','/v1/sessions/open',{
    idempotencyKey:'w6-second-open-0000001',audience:'agent',
    title:'Independent session',conversationUrl:target
  });
  assert.equal(second.status,200);
  assert.notEqual(second.payload.session.id,sid);
  const inventory=await cli(['session-ledger-list','--port',String(port)]);
  assert.equal(inventory.value.payload.sessions.length,2);
  assert.equal(inventory.value.payload.chatSubmissionAllowed,false);
  assert.equal(inventory.value.payload.durableAcrossProcessRestart,false);
  receipt.stages.push({name:'multiple-independent-sessions',state:'PASS'});

  const changed=await http(port,'POST','/v1/sessions/open',{...opened,title:'Other title'});
  assert.equal(changed.status,409);
  assert.equal(changed.payload.status,'IDEMPOTENCY_KEY_PAYLOAD_CONFLICT');
  receipt.stages.push({name:'cross-process-payload-reuse-denial',state:'PASS'});
} finally {
  if(host&&!hostExited){
    host.kill();
    await Promise.race([
      new Promise(resolve=>host.once('exit',resolve)),
      new Promise(resolve=>setTimeout(resolve,5000))
    ]);
    if(!hostExited)host.kill('SIGKILL');
  }
}
assert.equal(hostExited,true,'Owned host cleanup not verified');
receipt.stages.push({name:'owned-host-cleanup',state:'PASS'});
console.log(JSON.stringify(receipt,null,2));
