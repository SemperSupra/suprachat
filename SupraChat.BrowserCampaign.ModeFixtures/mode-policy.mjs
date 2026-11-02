// Pure synthetic policy oracle; no provider API or browser submission.
export const ORIGIN="https://chatgpt.com";
const labels={en:["Chat","Work"],de:["Chat","Arbeit"],th:["แชท","งาน"]};
const deny=reason=>Object.freeze({admission:"REFUSED",reason,effect:"NONE"});
export function observe(c){
  if(!c||c.source!=="SYNTHETIC_TYPED_ADAPTER"||c.origin!==ORIGIN||
     typeof c.session!=="string"||!c.session||
     !Number.isSafeInteger(c.epoch)||c.epoch<0||
     !Number.isSafeInteger(c.timestamp)||c.timestamp<0||
     !Object.hasOwn(labels,c.locale)||!Array.isArray(c.controls)||c.controls.length>16)
     return "UNKNOWN";
  const [chat,work]=labels[c.locale];
  const tabs=c.controls.filter(x=>x&&x.scope==="experience-switch"&&x.role==="tab"&&
    x.visible===true&&typeof x.selected==="boolean");
  const a=tabs.filter(x=>x.label===chat),b=tabs.filter(x=>x.label===work);
  if(a.length>1||b.length>1)return "CONFLICT";
  if(a.length!==1||b.length!==1)return "UNKNOWN";
  if(a[0].selected===b[0].selected)return a[0].selected?"CONFLICT":"UNKNOWN";
  return a[0].selected?"CHAT":"WORK";
}
export function preflight({intent,candidate,session,epoch,now,lease}={}){
  if(!["CHAT","WORK"].includes(intent))return deny("INVALID_INTENT");
  if(typeof session!=="string"||!session||!Number.isSafeInteger(epoch)||
     !Number.isSafeInteger(now)||now<0)return deny("INVALID_HOST");
  if(!candidate||candidate.session!==session||candidate.epoch!==epoch)
    return deny("SESSION_OR_NAVIGATION_MISMATCH");
  if(!Number.isSafeInteger(candidate.timestamp)||candidate.timestamp>now||
     now-candidate.timestamp>2000)return deny("STALE");
  const actual=observe(candidate);
  if(actual==="UNKNOWN"||actual==="CONFLICT")return deny(actual);
  if(actual!==intent)return deny("MODE_MISMATCH");
  if(intent==="WORK"&&(!lease||lease.type!=="EXPLICIT_HOST_WORK_LEASE"||
     lease.session!==session||lease.epoch!==epoch||lease.confirmed!==true||
     !Number.isSafeInteger(lease.expires)||lease.expires<=now))
    return deny("WORK_APPROVAL_REQUIRED");
  return Object.freeze({admission:"HOST_REVIEW_ONLY",reason:"SYNTHETIC_OBSERVATION",effect:"NONE"});
}
export function forAudience(out,audience){
  if(!["human","automation","agent"].includes(audience))throw new TypeError("audience");
  return Object.freeze({...out,schema:"suprachat-mode-policy-candidate/v1",
    evidence:"SYNTHETIC_ONLY",adapter:"NOT_CONNECTED",audience});
}
