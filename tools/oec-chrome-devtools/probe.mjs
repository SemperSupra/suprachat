import { createServer } from 'node:http';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const output=resolve('evidence');
await mkdir(output,{recursive:true});
const server=createServer((req,res)=>{
 res.setHeader('Cache-Control','no-store');
 res.setHeader('X-Content-Type-Options','nosniff');
 if(req.url==='/api/data'){res.writeHead(200,{'Content-Type':'application/json'});res.end('{"fixture":"oec","state":"ready"}');return;}
 if(req.url!=='/fixture'){res.writeHead(404);res.end('unavailable');return;}
 res.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});
 res.end('<!doctype html><html lang="en"><head><title>OEC DevTools Synthetic Witness</title></head>'+
  '<body><main><h1>OEC DevTools Synthetic Witness</h1><button id="probe">Load fixture status</button>'+
  '<p role="status" id="status">Not invoked</p></main>'+
  '<script>console.warn("OEC_SYNTHETIC_CONSOLE_MARKER");'+
  'fetch("/api/data").then(x=>x.json()).then(x=>{document.getElementById("status").textContent=x.state})'+
  '</script></body></html>');
});
await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
const url='http://127.0.0.1:'+server.address().port+'/fixture';
const transport=new StdioClientTransport({
 command:resolve('node_modules/.bin/chrome-devtools-mcp'),
 args:['--headless','--isolated','--no-usage-statistics','--no-performance-crux'],
 env:{...process.env},stderr:'pipe'
});
const client=new Client({name:'oec-chrome-devtools-independent-witness',version:'0.1.0'},{capabilities:{}});
const toText = result => (result.content||[]).filter(c=>c.type==='text').map(c=>c.text).join('\n');
const seen=[];
async function call(name,args={}){
 const v=await client.callTool({name,arguments:args});
 if(v.isError){
   // Diagnostic is from our original localhost synthetic fixture only;
   // do not turn this on for private/authenticated browser targets.
   const error=toText(v).replace(/http:\/\/127[.]0[.]0[.]1:[0-9]+/g,'[synthetic-loopback]');
   console.error('OEC_SYNTHETIC_MCP_TOOL_FAILURE '+name+' '+error.slice(0,900));
   throw Error('TOOL_FAIL_'+name);
 }
 seen.push(name);
 return toText(v);
}
function check(ok,code){if(!ok)throw Error('EVIDENCE_MISMATCH_'+code);}
let receipt;
try {
 await client.connect(transport);
 const tools=await client.listTools();
 const known=new Set(tools.tools.map(t=>t.name));
 for(const n of ['new_page','take_snapshot','list_network_requests','list_console_messages'])check(known.has(n),'missing_'+n);
 await call('new_page',{url,timeout:15000});
 const pageList=await call('list_pages');
 const pageMatch=pageList.match(/(?:^|\n)\s*(\d+)\s*:\s*http:\/\/127[.]0[.]0[.]1:/);
 if(!pageMatch){
   // Only the original synthetic fixture is open; redact its ephemeral port.
   console.error('OEC_SYNTHETIC_PAGE_LIST '+pageList.slice(0,1000)
      .replace(/http:\/\/127[.]0[.]0[.]1:[0-9]+/g,'[fixture-loopback]'));
 }
 check(Boolean(pageMatch),'synthetic_page_id_not_observed');
 const pageId=Number(pageMatch[1]);
 const snapshot=await call('take_snapshot',{pageId});
 check(snapshot.includes('OEC DevTools Synthetic Witness'),'snapshot_missing_fixture');
 const requests=await call('list_network_requests',{pageId,pageSize:100});
 check(requests.includes('/api/data'),'api_network_request_missing');
 const consoleLog=await call('list_console_messages',{pageId,pageSize:100});
 check(consoleLog.includes('OEC_SYNTHETIC_CONSOLE_MARKER'),'console_marker_missing');
 let performance='NOT_ATTEMPTED';
 if(known.has('performance_start_trace')&&known.has('performance_stop_trace')){
   try {
     await call('performance_start_trace',{pageId,reload:true,autoStop:false});
     await call('performance_stop_trace',{pageId});
     performance='PASS';
   } catch { performance='UNKNOWN_FIRST_FAILURE'; }
 }
 receipt={
  schema:'oec-chrome-devtools-witness/v1',
  upstream:'chrome-devtools-mcp@1.12.0',
  evidence_class:'DIRECT_BROWSER_SYNTHETIC_FIXTURE',
  chrome_headless_isolated:true,
  browser_snapshot:'PASS',
  synthetic_network_request:'PASS',
  synthetic_console_marker:'PASS',
  performance_trace:performance,
  observed_tools:seen,
  upstream_tool_count:tools.tools.length,
  raw_requests_exported:false,
  session_cookies_exported:false,
  real_chatgpt_browser:false
 };
}finally{
 await client.close().catch(()=>{});
 await new Promise(ok=>server.close(ok));
 if(receipt)await writeFile(resolve(output,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');
}
if(!receipt)process.exitCode=1;
else console.log('QUALIFIED_CHROME_DEVTOOLS_MCP_SYNTHETIC network=true console=true accessibility=true performance='+receipt.performance_trace);
