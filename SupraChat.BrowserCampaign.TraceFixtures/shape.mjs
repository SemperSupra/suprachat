import {createHmac,randomBytes} from "node:crypto";
// Candidate **synthetic-only** shape oracle. Not wired to live WebView2.
// No raw URLs, headers, frame bodies, cookies, or text are retained.
const ALLOWED=new Set(["requestWillBeSent","responseReceived","dataReceived",
 "loadingFinished","loadingFailed","webSocketCreated","webSocketFrameSent",
 "webSocketFrameReceived","webSocketClosed","eventSourceMessageReceived"]);
function sameOrigin(url,origin){
 try{
  const u=new URL(url);return (u.protocol===origin.protocol||u.protocol===
    (origin.protocol==="https:"?"wss:":"ws:"))&&
    u.hostname===origin.hostname&&u.port===origin.port&&!u.username&&!u.password;
 }catch{return false;}
}
export class SyntheticTraceShape {
 #key=randomBytes(32);#owner=new WeakMap();#states=new Set();
 constructor(origin="https://chatgpt.com",limit=32) {
  this.origin=new URL(origin);
  if(limit<2||limit>128||!Number.isInteger(limit))throw new RangeError("limit");
  this.limit=limit;
 }
 session(){
  if(this.#states.size>=4)throw new RangeError("session capacity");
  const handle=Object.freeze({});
  const state={active:new Map(),events:[],gaps:0,enabled:false};
  this.#owner.set(handle,state);this.#states.add(state);return handle;
 }
 #state(cap){const s=this.#owner.get(cap);if(!s)throw new TypeError("unknown capability");return s;}
 enable(cap,yes){const s=this.#state(cap);if(yes!==true&&yes!==false)throw new TypeError("mode");
  s.enabled=yes;if(!yes){s.active.clear();s.gaps++;}}
 #emit(s,event){if(s.events.length>=this.limit){s.events.shift();s.gaps++;}
  s.events.push(Object.freeze(event));}
 #gap(s){s.gaps++;this.#emit(s,{kind:"CAPTURE_GAP",surface:"SYNTHETIC",bytes:0});}
 ingest(cap,name,raw){
  const s=this.#state(cap);if(!s.enabled)return "OFF";
  if(typeof raw!=="string"||Buffer.byteLength(raw)>65536||!ALLOWED.has(name)){
   this.#gap(s);return "GAP";}
  let e;try{e=JSON.parse(raw);}catch{this.#gap(s);return "GAP";}
  const id=e?.requestId;
  if(typeof id!=="string"||id.length<1||id.length>128){this.#gap(s);return "GAP";}
  if(name==="requestWillBeSent"||name==="webSocketCreated"){
   const u=name==="requestWillBeSent"?e?.request?.url:e?.url;
   if(!sameOrigin(u,this.origin)){s.active.delete(id);return "IGNORED";}
   if(s.active.size>=32&&!s.active.has(id)){this.#gap(s);return "GAP";}
   s.active.set(id,createHmac("sha256",this.#key).update(id).digest("hex").slice(0,20));
  }
  const alias=s.active.get(id);if(!alias)return "IGNORED";
  if(name==="responseReceived"&&!sameOrigin(e?.response?.url,this.origin)){
    s.active.delete(id);this.#gap(s);return "GAP";}
  let bytes=0,status=null;
  if(name==="dataReceived"){
    if(!Number.isSafeInteger(e.dataLength)||e.dataLength<0){this.#gap(s);return "GAP";}
    bytes=e.dataLength;
  }
  if(name==="responseReceived"&&Number.isInteger(e?.response?.status)&&
     e.response.status>=100&&e.response.status<=599)status=e.response.status;
  this.#emit(s,{kind:name,correlation:alias,bytes,status,surface:"SYNTHETIC"});
  if(["loadingFinished","loadingFailed","webSocketClosed"].includes(name))s.active.delete(id);
  return "OBSERVED";
 }
 snapshot(cap){const s=this.#state(cap);
  return Object.freeze({schema:"suprachat-trace-shape-candidate/v1",
   grade:"SYNTHETIC_ONLY",completeness:"UNKNOWN",enabled:s.enabled,
   gaps:s.gaps,events:s.events.map(x=>({...x}))});}
}
