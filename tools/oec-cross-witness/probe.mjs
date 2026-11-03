import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const id=process.env.OEC_EXPERIMENT_ID;
const token=process.env.OEC_OBSERVER_TOKEN;
if (!/^e[a-f0-9]{24}$/.test(id||'') || !token || token.length<48)
  throw Error('SYNTHETIC_AUTH_CONFIGURATION_MISSING');
await mkdir(resolve('evidence'),{recursive:true});
const stage=[];
function assert(v,code){if(!v)throw Error('OBSERVATION_REFUSED_'+code)}
const wait=(ms)=>new Promise(ok=>setTimeout(ok,ms));
async function rpc(method,args={},auth=token){
  const payload={jsonrpc:'2.0',id:1,method:'tools/call',params:{name:method,arguments:args}};
  const response=await fetch('http://127.0.0.1:18763/mcp',{
    method:'POST',headers:{'Content-Type':'application/json','Authorization':'Bearer '+auth},
    body:JSON.stringify(payload),signal:AbortSignal.timeout(8000)});
  const data=await response.json();
  return {status:response.status,error:data.error,body:data.result?.structuredContent};
}
async function invoke(name,args={}){
  const d=await rpc(name,args);
  assert(d.status===200&&!d.error&&d.body,'mcp_'+name);
  return d.body;
}
async function createToolClient(name,bin,args){
  // Browser MCP child processes are untrusted relative to the local bearer.
  // Explicitly omit OEC_OBSERVER_TOKEN, OEC_EXPERIMENT_ID and GitHub token.
  const childEnv={PATH:process.env.PATH,HOME:process.env.HOME,
    LANG:process.env.LANG||'C.UTF-8',CI:'true'};
  assert(!('OEC_OBSERVER_TOKEN' in childEnv)&&!('GH_TOKEN' in childEnv),
    'unsafe_child_environment');
  const transport=new StdioClientTransport({command:bin,args,env:childEnv,stderr:'pipe'});
  const client=new Client({name,version:'0.1.0'},{capabilities:{}});
  await client.connect(transport);
  return client;
}
async function call(client,name,args={}){
  const r=await client.callTool({name,arguments:args});
  assert(!r.isError,'browser_'+name);
  stage.push(name);
  return (r.content||[]).filter(x=>x.type==='text').map(x=>x.text).join('\n');
}
const html="<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>OEC Correlated Synthetic Fixture</title></head><body>\n<main><h1>OEC Correlated Synthetic Fixture</h1>\n<button id=\"run\" type=\"button\">Run admitted synthetic event</button>\n<p role=\"status\" id=\"status\">Synthetic counter pending</p></main>\n<script>\nasync function state(){\n const r=await fetch('/api/state'); const x=await r.json();\n document.getElementById('status').textContent='Observed synthetic counter: '+x.counter;\n console.warn('OEC_SYNTHETIC_CONSOLE_OBSERVATION');\n}\ndocument.getElementById('run').addEventListener('click',async()=>{\n const r=await fetch('/api/action',{method:'POST'});\n if(!r.ok){document.getElementById('status').textContent='Action refused';return}\n await state();\n});\nstate();\n</script></body></html>";
let mutations=0;
const server=createServer(async(req,res)=>{
  res.setHeader('Cache-Control','no-store');
  res.setHeader('X-Content-Type-Options','nosniff');
  try{
    if(req.url==='/fixture'&&req.method==='GET'){
      res.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});res.end(html);return;
    }
    if(req.url==='/api/state'&&req.method==='GET'){
      const snapshot=await invoke('mirror.ui_snapshot');
      res.writeHead(200,{'Content-Type':'application/json'});
      res.end(JSON.stringify({counter:snapshot.counter,version:snapshot.version}));return;
    }
    if(req.url==='/api/action'&&req.method==='POST'&&mutations===0){
      mutations++;
      const result=await invoke('mirror.experiment',{
        fixture:'counter-v1',operation:'increment',request_id:id,admit_synthetic:true});
      res.writeHead(200,{'Content-Type':'application/json'});
      res.end(JSON.stringify({counter:result.counter,version:result.version}));return;
    }
    res.writeHead(404);res.end('not admitted');
  }catch{
    res.writeHead(500,{'Content-Type':'text/plain'});
    res.end('fixed synthetic proxy failure');
  }
});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
const url='http://127.0.0.1:'+server.address().port+'/fixture';
let pw=null,cd=null,receipt;
try{
  const catalog=await invoke('mirror.capabilities');
  assert(catalog.scope==='SYNTHETIC_FIXTURE_ONLY','capabilities_overclaim');
  const before=await invoke('mirror.ui_snapshot');
  assert(before.counter===0,'initial_state_not_zero');

  pw=await createToolClient('oec-playwright-control',process.execPath,[
    resolve('node_modules/@playwright/mcp/cli.js'),'--headless','--isolated',
    '--no-webmcp','--timeout-navigation=20000','--timeout-settle=250',
    '--output-dir',resolve('evidence')]);
  const pwTools=new Set((await pw.listTools()).tools.map(x=>x.name));
  for(const n of ['browser_navigate','browser_snapshot','browser_click'])
    assert(pwTools.has(n),'playwright_tool_missing');
  await call(pw,'browser_navigate',{url});
  const beforeBrowser=await call(pw,'browser_snapshot');
  assert(beforeBrowser.includes('Run admitted synthetic event'),'playwright_before_missing');
  await call(pw,'browser_click',{target:'#run',element:'Run admitted synthetic event button'});
  let afterBrowser='';
  for(let j=0;j<12;j++){
    afterBrowser=await call(pw,'browser_snapshot');
    if(afterBrowser.includes('Observed synthetic counter: 1'))break;
    await wait(250);
  }
  assert(afterBrowser.includes('Observed synthetic counter: 1'),'playwright_after_missing');
  const after=await invoke('mirror.ui_snapshot');
  assert(after.counter===1&&after.version===2,'server_owned_effect_missing');
  const replay=await invoke('mirror.experiment',{
    fixture:'counter-v1',operation:'increment',request_id:id,admit_synthetic:true});
  assert(replay.idempotent_replay===true&&replay.counter===1,'replay_not_idempotent');

  cd=await createToolClient('oec-independent-chrome-witness',
    resolve('node_modules/.bin/chrome-devtools-mcp'),
    ['--headless','--isolated','--no-usage-statistics','--no-performance-crux']);
  const chromeTools=new Set((await cd.listTools()).tools.map(x=>x.name));
  for(const n of ['new_page','list_pages','take_snapshot',
                  'list_network_requests','list_console_messages'])
    assert(chromeTools.has(n),'devtools_tool_missing');
  await call(cd,'new_page',{url,timeout:15000});
  const pageList=await call(cd,'list_pages');
  const match=pageList.match(/(?:^|\n)\s*(\d+)\s*:[^\n]*http:\/\/127[.]0[.]0[.]1:\d+\/fixture/);
  assert(Boolean(match),'chrome_page_identification_failed');
  const pageId=Number(match[1]);
  const chromeSnap=await call(cd,'take_snapshot',{pageId});
  assert(chromeSnap.includes('Observed synthetic counter: 1'),'independent_browser_did_not_observe_effect');
  const network=await call(cd,'list_network_requests',{pageId,pageSize:100});
  assert(network.includes('/api/state'),'independent_network_not_observed');
  const consoleText=await call(cd,'list_console_messages',{pageId,pageSize:100});
  assert(consoleText.includes('OEC_SYNTHETIC_CONSOLE_OBSERVATION'),'console_not_observed');

  const denied=await rpc('mirror.capabilities',{},'invalid-bearer');
  assert(denied.status===401,'wrong_bearer_accepted');
  const traceId=createHash('sha256').update('oec-synthetic-trace:'+id).digest('hex').slice(0,32);
  const start=BigInt(Date.now())*1000000n;
  const attr=(key,value)=>({key,value:{stringValue:value}});
  const mk=(spanId,name,parentId,offset,attrs)=>{
    const o={traceId,spanId,name,kind:1,
      startTimeUnixNano:String(start+BigInt(offset)*1000000n),
      endTimeUnixNano:String(start+BigInt(offset+5)*1000000n),
      attributes:[attr('oec.experiment.id',id),attr('oec.fixture','counter-v1'),
                  attr('oec.evidence.class','SYNTHETIC_INTEGRATION'),...attrs],
      status:{code:1}};
    if(parentId)o.parentSpanId=parentId;
    return o;
  };
  const spans=[
    mk('a1'.repeat(8),'oec.synthetic.mcp_action',null,0,
      [attr('oec.result','counter-1')]),
    mk('b2'.repeat(8),'oec.synthetic.independent_browser_witness','a1'.repeat(8),7,
      [attr('oec.result','observed-counter-1')])
  ];
  const otlp={resourceSpans:[{resource:{attributes:[
    attr('service.name','oec-correlated-synthetic-cycle')]},
    scopeSpans:[{scope:{name:'oec-synthetic-cross-witness'},spans}]}]};
  const response=await fetch('http://127.0.0.1:4318/v1/traces',{
    method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify(otlp),signal:AbortSignal.timeout(8000)});
  assert(response.status===200,'otel_collector_rejected');
  receipt={
    schema:'oec-cross-witness-cycle/v1',
    experiment_id:id,trace_id:traceId,fixture:'counter-v1',
    source_class:'CONTROLLED_SYNTHETIC_ONLY',
    observer_tool_effect:'PASS',observer_before:0,observer_after:1,
    idempotent_replay:'PASS',
    playwright_mcp_control:'PASS',
    chrome_devtools_independent_browser_state:'PASS',
    chrome_devtools_network:'PASS',chrome_devtools_console:'PASS',
    otlp_collector_ingest:'HTTP_200',wrong_bearer_refusal:'PASS',
    total_browser_tool_calls:stage.length,
    credentials_exported:false,authenticated_chatgpt_ui:false,
    hidden_chatgpt_model_access:false,
    first_party_claim_is_independent_proof:false,
    unknowns:['real_chatgpt_internals','authenticated_browser',
              'provider_independence','native_UIA'],
  };
}finally{
  if(cd)await cd.close().catch(()=>{});
  if(pw)await pw.close().catch(()=>{});
  await new Promise(ok=>server.close(ok));
  if(receipt) await writeFile(resolve('evidence','receipt.json'),
    JSON.stringify(receipt,null,2)+'\n');
}
assert(Boolean(receipt),'no_correlated_receipt');
console.log('QUALIFIED_SYNTHETIC_CROSS_WITNESS_OBSERVER_MCP_PLAYWRIGHT_DEVTOOLS_OTLP');
