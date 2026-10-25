#!/usr/bin/env node
// Public synthetic localhost adversarial response qualification for the
// READ-ONLY W6 desktop/core/CLI/agent inventory path. Never a real provider.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import {createServer} from 'node:http';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dll=path.join(root,'SupraChat.Automation','bin','Release','net10.0','SupraChat.Automation.dll');
const token=randomBytes(32).toString('hex');
const env={...process.env,SUPRACHAT_LEDGER_TOKEN:token};
const id='123e4567e89b42d3a456426614174000';
const phase=[];
const good={schema:'suprachat-shared-session-ledger/v1',sequence:1,
  sessions:[{id,title:'Synthetic',conversationUrl:null,workspaceKey:null,
    version:0,remoteBindingState:'NO_REMOTE_BINDING',
    chatSubmissionAllowed:false,terminalExecutionAllowed:false}],
  events:[{sequence:1,sessionId:id,audience:'human',operation:'open',sessionVersion:0}],
  durableAcrossProcessRestart:false,authenticatedAudiences:false,
  chatSubmissionAllowed:false,terminalExecutionAllowed:false,
  crossProcessTransportAvailable:false};
let fixture={kind:'json',payload:good};
let methodCount=0;
const server=createServer((req,res)=>{
  methodCount++;
  assert.equal(req.method,'GET','No mutation method permitted');
  assert.equal(req.url,'/v1/sessions','Must use only inventory route');
  assert.equal(req.headers.authorization,'Bearer '+token,'Must supply token only over loopback');
  res.setHeader('Content-Type','application/json');
  res.setHeader('Cache-Control','no-store');
  if(fixture.kind==='stall'){
    res.writeHead(200);
    res.write('{"schema":');
    // Deliberately leave the response unfinished. The client must
    // enforce the deadline even AFTER it has received the headers.
  } else if(fixture.kind==='raw'){
    res.writeHead(200);
    res.end(fixture.payload);
  } else if(fixture.kind==='header'){
    res.setHeader('Content-Length',String(fixture.length));
    res.writeHead(200);
    res.end();
  } else {
    res.writeHead(200);
    res.end(JSON.stringify(fixture.payload));
  }
});
server.requestTimeout=6500;
server.headersTimeout=5500;
server.listen(0,'127.0.0.1');
await new Promise((resolve,reject)=>{server.once('listening',resolve);server.once('error',reject)});
const port=server.address().port;

async function invoke(){
  const proc=spawn('dotnet',[dll,'session-ledger-view','--port',String(port)],{
    cwd:root,env,windowsHide:true,stdio:['ignore','pipe','pipe']});
  let out='',err='';
  const started=Date.now();
  return await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{proc.kill();reject(Error('READER_HUNG_BEYOND_HARD_DEADLINE'))},9000);
    proc.stdout.on('data',b=>{out+=b; if(out.length>20000)proc.kill();});
    proc.stderr.on('data',b=>{err+=b;if(err.length>2000)proc.kill();});
    proc.once('error',e=>{clearTimeout(timer);reject(e)});
    proc.once('close',code=>{
      clearTimeout(timer);
      if(code!==0)return reject(Error('VIEW_NONZERO_EXIT'));
      if(out.includes(token)||err.includes(token))return reject(Error('BEARER_LEAKED'));
      try{
        const obj=JSON.parse(out);
        assert.equal(obj.ok,true);
        // CLI deliberately uses snake_case; the underlying host HTTP
        // fixture uses camelCase. Assert the *published CLI contract*.
        assert.equal(obj.value.chat_submission_allowed,false);
        assert.equal(obj.value.terminal_execution_allowed,false);
        resolve({receipt:obj.value,elapsedMs:Date.now()-started});
      }catch(e){reject(e)}
    });
  });
}
async function caseCheck(name,kind,payload,connected,code){
  fixture={kind,payload};
  const observed=await invoke();
  assert.equal(observed.receipt.connected,connected,name);
  if(code)assert.equal(observed.receipt.status,code,name);
  phase.push({test:name,status:'PASS'});
  return observed;
}
try {
  await caseCheck('wellformed-metadata-only-inventory','json',good,true,
    'UNVERIFIED_LOOPBACK_HOST_SELF_REPORT');
  const epoch=await caseCheck('wellformed-unattested-host-epoch','json',
    {...good,hostInstanceId:'123e4567e89b42d3a456426614174000'},true,
    'UNVERIFIED_LOOPBACK_HOST_SELF_REPORT');
  assert.equal(epoch.receipt.host_instance_id,'123e4567e89b42d3a456426614174000');
  await caseCheck('malformed-host-instance-refused','json',
    {...good,hostInstanceId:'not-a-host'},false,
    'LEDGER_SCOPE_OR_SCHEMA_UNVERIFIED');
  await caseCheck('wrong-case-host-instance-refused','json',
    {...good,hostInstanceId:'123E4567E89B42D3A456426614174000'},false,
    'LEDGER_SCOPE_OR_SCHEMA_UNVERIFIED');
  await caseCheck('null-sessions-refused','json',{...good,sessions:null},false,
    'LEDGER_SCOPE_OR_SCHEMA_UNVERIFIED');
  await caseCheck('duplicate-session-ids-refused','json',
    {...good,sessions:[good.sessions[0],good.sessions[0]]},false,
    'LEDGER_INVALID_SESSION_METADATA');
  await caseCheck('session-privilege-claim-refused','json',
    {...good,sessions:[{...good.sessions[0],chatSubmissionAllowed:true}]},false,
    'LEDGER_INVALID_SESSION_METADATA');
  const reportedDurability = await caseCheck('durable-host-self-report-unverified','json',
    {...good,durableAcrossProcessRestart:true},true,
    'UNVERIFIED_LOOPBACK_HOST_SELF_REPORT');
  assert.equal(reportedDurability.receipt.durable_across_restart,true);
  assert.equal(reportedDurability.receipt.bound_to_desktop_sessions,false);
  assert.equal(reportedDurability.receipt.actor_identity_authenticated,false);
  await caseCheck('impossible-event-sequence-refused','json',
    {...good,events:[{...good.events[0],sequence:2}]},false,
    'LEDGER_INVALID_EVENT_SEQUENCE');
  await caseCheck('malformed-json-refused','raw',
    '{"schema":',false,'LEDGER_READ_FAILED');
  await caseCheck('chunked-oversized-body-refused','raw',
    'A'.repeat(65538),false,'LEDGER_RESPONSE_TOO_LARGE');
  fixture={kind:'header',length:70000};
  let oversized=await invoke();
  assert.equal(oversized.receipt.connected,false);
  assert.equal(oversized.receipt.status,'LEDGER_RESPONSE_TOO_LARGE');
  phase.push({test:'oversized-content-length-refused',status:'PASS'});
  const stalled=await caseCheck('stalled-response-body-ends-under-hard-deadline','stall',
    undefined,false,'LEDGER_READ_FAILED');
  assert(stalled.elapsedMs<8000,'Stalled body exceeded whole-response deadline');
  console.log(JSON.stringify({schema:'suprachat-w6-hostile-local-inventory-oracle/v1',
    passed:phase.length,tests:phase,localhost:true,methodsObserved:methodCount,
    maxStallElapsedMs:stalled.elapsedMs,providerContacted:false,
    privateSourceUsed:false,terminalExecution:false,hostWrites:false},null,2));
} finally {
  server.closeAllConnections();
  await new Promise(resolve=>server.close(resolve));
}
