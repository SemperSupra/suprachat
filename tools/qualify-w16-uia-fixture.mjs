#!/usr/bin/env node
// Bounded, non-provider synthetic UIA fixture. No real user account or writes.
import http from 'node:http';
const port = Number(process.argv[2]);
if (!Number.isInteger(port) || port < 1024 || port > 65535)
  throw Error('INVALID_FIXTURE_PORT');
const bearer = process.env.SUPRACHAT_LEDGER_TOKEN;
if (!bearer || !/^[a-zA-Z0-9_-]{32,128}$/.test(bearer))
  throw Error('FIXTURE_BEARER_MISSING');
const hostInstanceId = '123e4567e89b42d3a456426614174000';
const sessionA = '123e4567e89b42d3a456426614174001';
const sessionB = '123e4567e89b42d3a456426614174002';
let inventoryGets = 0;
let refused = 0;
let unsafe = 0;
const sessions = [
  {id:sessionA,title:'UIA Synthetic A',conversationUrl:null,workspaceKey:null,
   version:0,remoteBindingState:'NO_REMOTE_BINDING',
   chatSubmissionAllowed:false,terminalExecutionAllowed:false},
  {id:sessionB,title:'UIA Synthetic B',conversationUrl:null,workspaceKey:null,
   version:0,remoteBindingState:'NO_REMOTE_BINDING',
   chatSubmissionAllowed:false,terminalExecutionAllowed:false}
];
const events = [
  {sequence:1,sessionId:sessionA,audience:'human',operation:'open',sessionVersion:0},
  {sequence:2,sessionId:sessionB,audience:'agent',operation:'open',sessionVersion:0}
];
const server = http.createServer((req,res)=>{
  res.setHeader('Cache-Control','no-store');
  res.setHeader('Content-Type','application/json');
  if(req.method !== 'GET') {
    unsafe++; res.writeHead(405); res.end('{}'); return;
  }
  if(req.headers.authorization !== 'Bearer '+bearer || req.headers.origin) {
    refused++; res.writeHead(403); res.end('{}'); return;
  }
  if (req.url==='/__fixture/counts') {
    res.writeHead(200);
    res.end(JSON.stringify({schema:'suprachat-w16-uia-fixture/v1',
      inventory_gets:inventoryGets,refused,unsafe,synthetic:true}));
    return;
  }
  if(req.url !== '/v1/sessions') {
    refused++;res.writeHead(404);res.end('{}');return;
  }
  inventoryGets++;
  res.writeHead(200);
  res.end(JSON.stringify({schema:'suprachat-shared-session-ledger/v1',
    sequence:2,hostInstanceId,sessions,events,
    durableAcrossProcessRestart:false,authenticatedAudiences:false,
    chatSubmissionAllowed:false,terminalExecutionAllowed:false,
    crossProcessTransportAvailable:true}));
});
server.listen(port,'127.0.0.1',()=>{
  console.log(JSON.stringify({schema:'suprachat-w16-uia-fixture/v1',
    ready:true,bind:'127.0.0.1',port,synthetic:true}));
});
process.on('SIGTERM',()=>server.close(()=>process.exit(0)));
