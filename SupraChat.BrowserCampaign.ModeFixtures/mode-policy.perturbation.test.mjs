import test from "node:test";
import assert from "node:assert/strict";
import {observe,preflight} from "./mode-policy.mjs";
const make=()=>({source:"SYNTHETIC_TYPED_ADAPTER",origin:"https://chatgpt.com",session:"s",epoch:3,timestamp:1000,locale:"en",controls:[
 {scope:"experience-switch",role:"tab",label:"Chat",visible:true,selected:true},
 {scope:"experience-switch",role:"tab",label:"Work",visible:true,selected:false}]});
test("256 synthetic perturbations preserve refusal on unqualified state",()=>{
 for(let i=0;i<256;i++){
  const x=make();
  const change=i%8;
  if(change===0)x.origin="https://other.invalid";
  if(change===1)x.session="other";
  if(change===2)x.epoch=9;
  if(change===3)x.controls[0].visible=false;
  if(change===4)x.controls[0].selected=false;
  if(change===5)x.controls[1].selected=true;
  if(change===6)x.controls.push({...x.controls[0]});
  if(change===7)x.controls[1].role="paragraph";
  const d=preflight({candidate:x,intent:"CHAT",session:"s",epoch:3,now:1100});
  assert.equal(d.admission,"REFUSED");assert.equal(d.effect,"NONE");
 }
});
