import test from "node:test";
import assert from "node:assert/strict";
import {SyntheticTraceShape} from "./shape.mjs";
test("512 synthetic input events do not exceed the bounded receipt",()=>{
 const p=new SyntheticTraceShape("https://chatgpt.com",8),s=p.session();
 p.enable(s,true);
 for(let i=0;i<512;i++)
 p.ingest(s,"requestWillBeSent",JSON.stringify({
 requestId:"sensitive-"+i,request:{url:"https://chatgpt.com/item?private="+i}}));
 const result=p.snapshot(s),text=JSON.stringify(result);
 assert.ok(result.events.length<=8);
 assert.ok(result.gaps>0);
 assert.equal(text.includes("sensitive-"),false);
 assert.equal(text.includes("/item?private="),false);
 assert.equal(result.completeness,"UNKNOWN");
});
