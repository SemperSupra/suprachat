import test from "node:test";
import assert from "node:assert/strict";
import {SyntheticTraceShape} from "./shape.mjs";
const id="synthetic-request-id",token="SYNTHETIC_SENTINEL";
const request=(url="https://chatgpt.com/resource?x="+token)=>
 JSON.stringify({requestId:id,request:{url,postData:token}});
const message=(extra={})=>JSON.stringify({requestId:id,
 response:{url:"https://chatgpt.com/resource",status:200,body:token},
 dataLength:3,payloadData:token,...extra});
test("default OFF, metadata-only lifecycle and no sentinel leak",()=>{
 const p=new SyntheticTraceShape(),s=p.session();
 assert.equal(p.ingest(s,"requestWillBeSent",request()),"OFF");
 p.enable(s,true);
 for(const [name,payload] of [
 ["requestWillBeSent",request()],["responseReceived",message()],
 ["dataReceived",message()],["eventSourceMessageReceived",message()],
 ["webSocketFrameReceived",message()]])
 assert.equal(p.ingest(s,name,payload),"OBSERVED");
 const result=JSON.stringify(p.snapshot(s));
 assert.equal(result.includes(token),false);
 assert.equal(result.includes(id),false);
 assert.equal(result.includes("/resource"),false);
 assert.equal(p.snapshot(s).events[2].bytes,3);
 assert.equal(p.snapshot(s).completeness,"UNKNOWN");
});
test("per-session aliases differ; foreign capability rejected",()=>{
 const p=new SyntheticTraceShape(),a=p.session(),b=p.session();
 p.enable(a,true);p.enable(b,true);
 p.ingest(a,"requestWillBeSent",request());p.ingest(b,"requestWillBeSent",request());
 assert.notEqual(p.snapshot(a).events[0].correlation,p.snapshot(b).events[0].correlation);
 assert.throws(()=>new SyntheticTraceShape().snapshot(a),TypeError);
});
test("malformed, oversized, spoofed and missing correlation",()=>{
 const p=new SyntheticTraceShape("https://chatgpt.com",3),s=p.session();p.enable(s,true);
 assert.equal(p.ingest(s,"unknown","{}"),"GAP");
 assert.equal(p.ingest(s,"responseReceived","{"),"GAP");
 assert.equal(p.ingest(s,"requestWillBeSent","x".repeat(70000)),"GAP");
 assert.equal(p.ingest(s,"requestWillBeSent",request("https://chatgpt.com.evil.invalid")),"IGNORED");
 assert.equal(p.ingest(s,"dataReceived",message()),"IGNORED");
 p.ingest(s,"requestWillBeSent",request());
 assert.equal(p.ingest(s,"dataReceived",message({dataLength:-1})),"GAP");
 assert.ok(p.snapshot(s).gaps>=4);
 assert.ok(p.snapshot(s).events.length<=3);
 p.enable(s,false);assert.equal(p.ingest(s,"dataReceived",message()),"OFF");
});
