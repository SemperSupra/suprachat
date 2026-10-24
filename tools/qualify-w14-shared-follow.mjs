#!/usr/bin/env node
// W14 real W6 read-only observer parity; public synthetic metadata only.
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {spawn} from 'node:child_process';
import {createServer} from 'node:net';
import {fileURLToPath} from 'node:url';
import path from 'node:path';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dll=path.join(root,'SupraChat.Automation','bin','Release','net10.0','SupraChat.Automation.dll');
const token=randomBytes(32).toString('hex');
const env={...process.env,SUPRACHAT_LEDGER_TOKEN:token};
let host=null,port;
const stages=[];
function record(name){stages.push(name)}
async function findPort(){return await new Promise((resolve,reject)=>{
 const s=createServer();s.once('error',reject);
 s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>resolve(p))});
})}
async function launch(){
 port=await findPort();
 host=spawn('dotnet',[dll,'session-ledger-host','--port',String(port)],
   {cwd:root,env,stdio:['ignore','pipe','pipe'],windowsHide:true});
 await new Promise((resolve,reject)=>{
  let stdout='',done=false;
  const timer=setTimeout(()=>fail(new Error('HOST_READY_TIMEOUT')),14000);
  function fail(e){if(!done){done=true;clearTimeout(timer);reject(e)}}
  host.once('error',fail);host.once('exit',c=>fail(new Error('HOST_PREMATURE_EXIT_'+c)));
  host.stdout.on('data',b=>{
   stdout+=b;
   if(stdout.includes(token))return fail(new Error('BEARER_OUTPUT_LEAK'));
   const line=stdout.split(/\r?\n/).slice(0,-1).find(x=>x.startsWith('{"schema":'));
   if(!line)return;
   let receipt;
   try{receipt=JSON.parse(line)}catch{return fail(new Error('HOST_READY_BAD_JSON'))}
   if(receipt.bound_address!=='127.0.0.1'||
      receipt.authenticated_audience_identity!==false||
      receipt.provider_prompt_submission_available!==false)
     return fail(new Error('HOST_FALSE_AUTHORITY_CLAIM'));
   done=true;clearTimeout(timer);resolve();
  });
 });
}
async function stop(){
 const p=host;host=null;if(!p || p.exitCode!==null)return;
 await new Promise(resolve=>{
  const timer=setTimeout(()=>{p.kill('SIGKILL');resolve()},6000);
  p.once('exit',()=>{clearTimeout(timer);resolve()});p.kill();
 });
}
async function http(route,body=null,bearer=token,origin=null){
 const headers={Authorization:'Bearer '+bearer};
 if(body)headers['Content-Type']='application/json';
 if(origin)headers.Origin=origin;
 const ctrl=new AbortController(),timer=setTimeout(()=>ctrl.abort(),7000);
 try{
  const response=await fetch('http://127.0.0.1:'+port+route,{
   method:body?'POST':'GET',headers,body:body?JSON.stringify(body):undefined,
   signal:ctrl.signal});
  const raw=await response.text();assert(raw.length<=131072);
  assert(!raw.includes(token));
  return {status:response.status,payload:raw?JSON.parse(raw):null};
 }finally{clearTimeout(timer)}
}
async function invoke(args,stdin){
 return await new Promise((resolve,reject)=>{
  const cli=spawn('dotnet',[dll,...args],{cwd:root,env,
   stdio:['pipe','pipe','pipe'],windowsHide:true});
  let out='',err='',done=false;
  const t=setTimeout(()=>{cli.kill();fail(new Error('CLIENT_TIMEOUT'))},14000);
  function fail(e){if(!done){done=true;clearTimeout(t);reject(e)}}
  cli.once('error',fail);
  cli.stdout.on('data',b=>{out+=b;if(out.length>140000)fail(new Error('CLIENT_TOO_LARGE'))});
  cli.stderr.on('data',b=>{err+=b;if(err.length>8000)fail(new Error('CLIENT_STDERR_TOO_LARGE'))});
  cli.once('close',code=>{
   if(done)return;done=true;clearTimeout(t);
   if(code!==0)return reject(new Error('CLIENT_EXIT_'+code));
   try{return resolve(JSON.parse(out.trim()))}catch(e){reject(e)}
  });
  cli.stdin.end(stdin?JSON.stringify(stdin)+'\n':'');
 });
}
const open=(id,title,audience)=>({idempotencyKey:id,title,audience});
try {
 await launch();
 assert.equal((await http('/v1/sessions',null,'wrong')).status,401);
 assert.equal((await http('/v1/sessions',null,token,'https://attacker.invalid')).status,403);
 const a=(await http('/v1/sessions/open',open('w14-observer-open-a-0001','A','human')));
 const b=(await http('/v1/sessions/open',open('w14-observer-open-b-0002','B','agent')));
 assert.equal(a.status,200);assert.equal(b.status,200);
 const idA=a.payload.session.id,idB=b.payload.session.id;
 assert.notEqual(idA,idB);
 const followArgs=['session-ledger-follow','--port',String(port),
   '--session-id',idA];
 const cli=await invoke(followArgs);
 assert.equal(cli.ok,true);
 assert.equal(cli.value.selected_session_id,idA);
 assert.equal(cli.value.sequence,2);
 assert.equal(cli.value.events.length,1);
 assert.equal(cli.value.events[0].operation,'open');
 assert.equal(cli.value.bound_to_desktop_workbench,false);
 assert.equal(cli.value.remote_chat_authority,false);
 assert.equal(cli.value.actor_identity_authenticated,false);
 record('human-cli-selected-A-not-local-tab-while-B-open');
 const p={jsonrpc:'2.0',id:42,method:'session/ledger/follow',
   params:{port,sessionId:idB}};
 const agent=await invoke(['stdio'],p);
 assert.equal(agent.id,42);assert.equal(agent.result.selected_session_id,idB);
 assert.equal(agent.result.events.length,1);
 assert.equal(agent.result.events[0].sessionId,idB);
 assert.equal(agent.result.terminal_authority,false);
 record('separate-agent-process-selected-B-uses-identical-read-only-core');
 const update=await http('/v1/sessions/change',{
   idempotencyKey:'w14-change-A-000000001',audience:'automation',
   sessionId:idA,expectedVersion:0,operation:'set-title',value:'A-new'});
 assert.equal(update.status,200);
 const after=await invoke([...followArgs,'--after','2']);
 assert.equal(after.value.status,'SELECTED_NEW_EVENTS');
 assert.equal(after.value.sequence,3);
 assert.equal(after.value.events.length,1);
 assert.equal(after.value.events[0].sequence,3);
 assert.equal(after.value.events[0].sessionId,idA);
 assert.equal(after.value.event_gap_unverified,false);
 const idle=await invoke([...followArgs,'--after','3']);
 assert.equal(idle.value.status,'SELECTED_NO_NEW_EVENTS');
 assert.equal(idle.value.events.length,0);
 const bAfter=await invoke(['stdio'],{
  jsonrpc:'2.0',id:43,method:'session/ledger/follow',
  params:{port,sessionId:idB,afterSequence:2}});
 assert.equal(bAfter.result.events.length,0);
 assert.equal(bAfter.result.selected_session_id,idB);
 record('incremental-A-events-only-B-isolated-and-repeat-no-replay');
 const bad=await invoke([...followArgs,'--after','999']);
 assert.equal(bad.value.status,'SEQUENCE_REWIND_RESELECT_REQUIRED');
 assert.equal(bad.value.selected_session_id,null);
 assert.equal(bad.value.event_gap_unverified,true);
 const missing=await invoke(['session-ledger-follow','--port',String(port),
  '--session-id','00000000000000000000000000000000']);
 assert.equal(missing.value.status,'SELECTED_SESSION_MISSING_RESELECT_REQUIRED');
 record('rewind-and-missing-host-session-refuse-silent-selection');
 await stop();await launch();
 const restart=await invoke([...followArgs,'--after','3']);
 assert.equal(restart.value.status,'SEQUENCE_REWIND_RESELECT_REQUIRED');
 assert.equal(restart.value.selected_session_id,null);
 record('ephemeral-host-restart-invalidates-old-event-cursor');
 console.log(JSON.stringify({schema:'suprachat-w14-actual-host-observer/v1',
  result:'PASS',stages,independent_sessions:2,cli_and_agent_parity:true,
  browser_messages_submitted:0,terminal_actions:0,
  authenticated_actor_identity:false,local_workbench_mutated:false,
  complete_chatgpt_history_mirrored:false}));
} finally {await stop();}
