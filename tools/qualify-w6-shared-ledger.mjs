#!/usr/bin/env node
// W6 actual cross-process, cross-audience qualification on disposable hosts.
// No container, private checkout, provider browser, or authenticated ChatGPT.
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {spawn} from 'node:child_process';
import {createServer} from 'node:net';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const automationDll = path.join(root, 'SupraChat.Automation','bin','Release','net10.0','SupraChat.Automation.dll');
const token = randomBytes(32).toString('base64url');
const env = {...process.env, SUPRACHAT_LEDGER_TOKEN: token, SUPRACHAT_W6_DISPOSABLE_ONLY: 'yes'};
const timeoutMs = 15000;
const prefix = 'http://127.0.0.1:';
const stages = [];
let host;
let outputSeen = '';

function record(name) { stages.push({name, status: 'PASS'}); }
function runCli(command, input) {
  return new Promise((resolve, reject) => {
    const child = spawn('dotnet', [automationDll,...command], {
      cwd: root, env, stdio: ['pipe','pipe','pipe'], windowsHide: true,
    });
    let stdout = '', stderr = '';
    const guard = setTimeout(() => child.kill(), timeoutMs);
    child.stdout.on('data', b => { stdout+=b; if(stdout.length>128000) child.kill(); });
    child.stderr.on('data', b => { stderr+=b; if(stderr.length>32000) child.kill(); });
    child.on('error', e => {clearTimeout(guard); reject(e);});
    child.on('close', code => {
      clearTimeout(guard);
      if (code!==0) return reject(new Error('CLI_EXIT_NONZERO_'+command[0]));
      try {resolve(JSON.parse(stdout));} catch {reject(new Error('CLI_JSON_INVALID_'+command[0]));}
    });
    child.stdin.end(input ?? '');
  });
}
function waitPort() {
  return new Promise((resolve,reject)=>{
    const server = createServer();
    server.once('error',reject);
    server.listen(0,'127.0.0.1',()=>{
      const port = server.address().port;
      server.close(()=>resolve(port));
    });
  });
}
async function startHost(port) {
  host = spawn('dotnet', [automationDll,'session-ledger-host','--port',String(port)], {
    cwd:root,env,stdio:['ignore','pipe','pipe'],windowsHide:true,
  });
  outputSeen='';
  return await new Promise((resolve,reject)=>{
    const guard = setTimeout(()=>reject(new Error('HOST_START_TIMEOUT')),timeoutMs);
    let settled=false, stderr='';
    const fail=e=>{if(!settled){settled=true;clearTimeout(guard);reject(e)}};
    host.on('error',fail);
    host.on('exit',code=>fail(new Error('HOST_EXIT_PREMATURE_'+code)));
    host.stderr.on('data', b=>{stderr+=b;if(stderr.length>16000)fail(new Error('HOST_STDERR_TOO_LARGE'));});
    host.stdout.on('data',b=>{
      outputSeen+=b;
      if(outputSeen.includes(token))return fail(new Error('HOST_LEAKED_BEARER'));
      if(outputSeen.length>16000)return fail(new Error('HOST_STDOUT_TOO_LARGE'));
      const line=outputSeen.split(/\r?\n/).slice(0,-1).find(l=>l.trim().startsWith('{"schema":'));
      if(line){
        let receipt;
        try {receipt=JSON.parse(line)}catch{return fail(new Error('HOST_READY_INVALID_JSON'))}
        if(receipt.schema!=='suprachat-w6-loopback-session-host/v1')return fail(new Error('HOST_READY_SCHEMA'));
        if(receipt.bound_address!=='127.0.0.1'||receipt.port!==port||
           receipt.authenticated_audience_identity!==false||receipt.persistent_recovery_available!==false)
          return fail(new Error('HOST_UNSAFE_ADMISSION_CLAIM'));
        settled=true;clearTimeout(guard);resolve();
      }
    });
  });
}
async function stopHost() {
  if(!host)return;
  const proc=host;host=null;
  if(proc.exitCode!==null)return;
  await new Promise(resolve=>{
    const guard=setTimeout(()=>{proc.kill('SIGKILL');resolve();},4000);
    proc.once('exit',()=>{clearTimeout(guard);resolve();});
    proc.kill();
  });
}
async function http(port, route, method='GET', body, supplied=token, origin) {
  const headers={};
  if(supplied!==null)headers.authorization='Bearer '+supplied;
  if(origin) headers.origin=origin;
  if(body!==undefined)headers['content-type']='application/json';
  const controller=new AbortController();
  const guard=setTimeout(()=>controller.abort(),5000);
  try{
    const resp=await fetch(prefix+port+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body),signal:controller.signal});
    const raw=await resp.text();
    assert(raw.length<65536,'response must stay bounded');
    assert(!raw.includes(token),'token must not be echoed in response');
    return {status:resp.status, json:raw?JSON.parse(raw):null};
  }finally{clearTimeout(guard)}
}
function cliValue(obj) {
  assert.equal(obj.ok,true);
  assert.equal(obj.value.schema,'suprachat-w6-loopback-ledger-client/v1');
  assert.equal(obj.value.bearer_token_disclosed,false);
  assert.equal(obj.value.chat_submission_attempted,false);
  assert.equal(obj.value.terminal_command_executed,false);
  return obj.value;
}
function rpcValue(obj) {
  assert.equal(obj.jsonrpc,'2.0');
  assert.equal(obj.id,71);
  assert.equal(obj.result.schema,'suprachat-w6-loopback-ledger-client/v1');
  assert.equal(obj.result.bearer_token_disclosed,false);
  assert.equal(obj.result.chat_submission_attempted,false);
  return obj.result;
}
function rpc(port, method, request) {
  const params={port};if(request!==undefined) params.request=request;
  const input=JSON.stringify({jsonrpc:'2.0',id:71,method,params})+'\n';
  return runCli(['stdio'],input).then(rpcValue);
}
function change(id,idempotencyKey,audience,value) {
  return {sessionId:id,idempotencyKey,audience,expectedVersion:0,operation:'set-title',value};
}
try {
  const port=await waitPort();
  await startHost(port);
  const health=await http(port,'/v1/health');
  assert.equal(health.status,200);
  assert.equal(health.json.durable_recovery_available,false);
  assert.equal(health.json.audience_identity_authenticated,false);
  record('host-bound-127.0.0.1-ephemeral');

  assert.equal((await http(port,'/v1/sessions','GET',undefined,null)).status,401);
  assert.equal((await http(port,'/v1/sessions','GET',undefined,'wrong-token')).status,401);
  assert.equal((await http(port,'/v1/sessions','GET',undefined,token,'https://attacker.invalid')).status,403);
  record('missing-wrong-token-and-browser-origin-denied');

  const openReq={idempotencyKey:'w6-human-open-00000001',audience:'human',title:'Shared work',conversationUrl:'https://chatgpt.com/c/123e4567-e89b-42d3-a456-426614174000'};
  const open=cliValue(await runCli(['session-ledger-open','--port',String(port)],JSON.stringify(openReq)));
  assert.equal(open.http_status,200);
  assert.equal(open.payload.applied,true);
  const sid=open.payload.session.id;
  assert.match(sid,/^[a-f0-9]{32}$/i);
  const same=cliValue(await runCli(['session-ledger-open','--port',String(port)],JSON.stringify(openReq)));
  assert.equal(same.payload.isReplay,true);
  assert.equal(same.payload.session.id,sid);
  assert.equal((await http(port,'/v1/sessions')).json.sessions.length,1);
  record('human-cli-open-idempotent-replay');

  const [automation,agent]=await Promise.all([
    http(port,'/v1/sessions/change','POST',change(sid,'w6-auto-cas-00000001','automation','Auto update')),
    rpc(port,'session/ledger/change',change(sid,'w6-agent-cas-00000001','agent','Agent update')),
  ]);
  const decisions=[{status:automation.status,body:automation.json},{status:agent.http_status,body:agent.payload}];
  assert.equal(decisions.filter(x=>x.status===200&&x.body.applied).length,1,'Exactly one concurrent CAS may win');
  assert.equal(decisions.filter(x=>x.status===409&&x.body.status==='VERSION_CONFLICT').length,1);
  const list=cliValue(await runCli(['session-ledger-list','--port',String(port)]));
  assert.equal(list.payload.sessions[0].version,1);
  record('three-independent-processes-one-authoritative-CAS');

  const loserWasAutomation=automation.status===409;
  const losingRequest=loserWasAutomation
    ?change(sid,'w6-auto-cas-00000001','automation','Auto update')
    :change(sid,'w6-agent-cas-00000001','agent','Agent update');
  const rejected=loserWasAutomation
    ?await http(port,'/v1/sessions/change','POST',losingRequest)
    :await rpc(port,'session/ledger/change',losingRequest);
  const rejectionBody=loserWasAutomation?rejected.json:rejected.payload;
  assert.equal(rejectionBody.isReplay,true);
  assert.equal(rejectionBody.status,'VERSION_CONFLICT');
  record('immutable-losing-action-retry');

  const secondOpen=await rpc(port,'session/ledger/open',
    {idempotencyKey:'w6-agent-newsession-00001',audience:'agent',title:'Independent session'});
  assert.equal(secondOpen.http_status,200);
  assert.notEqual(secondOpen.payload.session.id,sid);
  const all=(await http(port,'/v1/sessions')).json;
  assert.equal(all.sessions.length,2);
  assert.equal(all.sessions.find(s=>s.id===sid).version,1);
  assert.equal(all.sessions.find(s=>s.id!==sid).version,0);
  assert.equal(all.authenticatedAudiences,false);
  assert.equal(all.durableAcrossProcessRestart,false);
  record('simultaneous-independent-session-inventory');

  const invalidReuse=await http(port,'/v1/sessions/open','POST',
    {...openReq,title:'Different payload same key'});
  assert.equal(invalidReuse.status,409);
  assert.equal(invalidReuse.json.status,'IDEMPOTENCY_KEY_PAYLOAD_CONFLICT');
  record('idempotency-payload-change-conflict');

  await stopHost();
  const secondPort=await waitPort();
  await startHost(secondPort);
  const afterRestart=(await http(secondPort,'/v1/sessions')).json;
  assert.equal(afterRestart.sessions.length,0,'W6 must not claim durable restore');
  record('host-crash-restart-clears-ephemeral-state');

  const receipt={
    schema:'suprachat-w6-independent-process-qualification/v1',
    stages,passed:stages.length,
    actual_separate_processes:true,
    source_credential_disclosed:false,
    authenticated_audience_identities:false,
    persistent_recovery:false,
    chatgpt_prompt_sent:false,
    terminal_command_executed:false,
    windows_docker_installed:false,
    private_source_used:false
  };
  console.log(JSON.stringify(receipt,null,2));
} finally {
  await stopHost();
}
