import test from "node:test";
import assert from "node:assert/strict";
import {observe,preflight,forAudience} from "./mode-policy.mjs";
const labels={en:["Chat","Work"],de:["Chat","Arbeit"],th:["แชթ","งาน"]};
function fixture(locale="en",selection="CHAT",session="a",epoch=1){
 const [a,b]=labels[locale];
 return {source:"SYNTHETIC_TYPED_ADAPTER",origin:"https://chatgpt.com",session,epoch,
 timestamp:5000,locale,controls:[
 {scope:"experience-switch",role:"tab",visible:true,label:a,selected:selection==="CHAT"},
 {scope:"experience-switch",role:"tab",visible:true,label:b,selected:selection==="WORK"}]};
}
const check=(candidate,intent="CHAT",extra={})=>preflight({
 candidate,intent,session:"a",epoch:1,now:5100,...extra});
for(const locale of Object.keys(labels))test("locale "+locale,()=>{
 assert.equal(observe(fixture(locale)),"CHAT");
 assert.equal(observe(fixture(locale,"WORK")),"WORK");
 assert.equal(check(fixture(locale)).admission,"HOST_REVIEW_ONLY");
});
test("missing duplicate or spoofed switches",()=>{
 let a=fixture();a.controls=[];assert.equal(observe(a),"UNKNOWN");
 a=fixture();a.controls.push({...a.controls[0]});assert.equal(observe(a),"CONFLICT");
 a=fixture();a.controls[1].selected=true;assert.equal(observe(a),"CONFLICT");
 a=fixture();a.controls[0].selected=false;assert.equal(observe(a),"UNKNOWN");
 a=fixture();a.controls[0].visible=false;assert.equal(observe(a),"UNKNOWN");
 a=fixture();a.controls.push({scope:"assistant-message",role:"tab",label:"Work",visible:true,selected:true});
 assert.equal(observe(a),"CHAT");
});
test("explicit work requires bounded host operator lease",()=>{
 const a=fixture("en","WORK");
 assert.equal(check(a,"WORK").admission,"REFUSED");
 const lease={type:"EXPLICIT_HOST_WORK_LEASE",session:"a",epoch:1,confirmed:true,expires:7000};
 assert.equal(check(a,"WORK",{lease}).admission,"HOST_REVIEW_ONLY");
 for(const patch of [{confirmed:false},{epoch:2},{expires:5100},{type:"OTHER"}])
 assert.equal(check(a,"WORK",{lease:{...lease,...patch}}).admission,"REFUSED");
});
test("navigation races and all outputs inert",()=>{
 const a=fixture();
 assert.equal(check(a,"CHAT",{epoch:2}).admission,"REFUSED");
 assert.equal(check(a,"CHAT",{session:"b"}).admission,"REFUSED");
 assert.equal(check(a,"CHAT",{now:7100}).admission,"REFUSED");
 assert.equal(check(a,"WORK").admission,"REFUSED");
 assert.equal(check(a).effect,"NONE");
 for(const audience of ["human","automation","agent"]){
 const v=forAudience(check(a),audience);
 assert.equal(v.effect,"NONE");assert.equal(v.adapter,"NOT_CONNECTED");
 }
});
