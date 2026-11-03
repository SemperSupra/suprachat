package observer
import("encoding/json";"net/http/httptest";"strings";"testing")
const goodToken="abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
func testCall(t *testing.T,h *Handler,name string,args any)map[string]any{
t.Helper();b,_:=json.Marshal(map[string]any{"jsonrpc":"2.0","id":1,"method":"tools/call","params":map[string]any{"name":name,"arguments":args}})
r:=httptest.NewRequest("POST","/mcp",strings.NewReader(string(b)));r.Header.Set("Authorization","Bearer "+goodToken);r.Header.Set("Content-Type","application/json")
w:=httptest.NewRecorder();h.ServeHTTP(w,r)
var out map[string]any;_ =json.Unmarshal(w.Body.Bytes(),&out);return out
}
func value(t *testing.T,h *Handler,name string,args any)map[string]any{
t.Helper();m:=testCall(t,h,name,args)
if m["error"]!=nil||m["result"]==nil {t.Fatalf("%s: %+v",name,m)}
return m["result"].(map[string]any)["structuredContent"].(map[string]any)
}
func TestObservedSyntheticLifecycle(t *testing.T){
h,e:=New(goodToken);if e!=nil{t.Fatal(e)}
v:=value(t,h,"mirror.capabilities",map[string]any{})
if v["chatgpt_internal_access"]!=false||v["scope"]!="SYNTHETIC_FIXTURE_ONLY"{t.Fatal(v)}
s:=value(t,h,"mirror.ui_snapshot",map[string]any{})
if s["real_browser"]!=false||s["counter"]!=float64(0){t.Fatal(s)}
args:=map[string]any{"fixture":"counter-v1","operation":"increment","request_id":"alpha","admit_synthetic":true}
first:=value(t,h,"mirror.experiment",args)
again:=value(t,h,"mirror.experiment",args)
if first["counter"]!=float64(1)||again["idempotent_replay"]!=true{t.Fatal("idempotence")}
diff:=value(t,h,"mirror.ui_diff",map[string]any{"before":1,"after":2})
if diff["changed"]!=true{t.Fatal(diff)}
pass:=value(t,h,"mirror.verify",map[string]any{"expected_counter":1,"expected_version":2})
fail:=value(t,h,"mirror.verify",map[string]any{"expected_counter":2,"expected_version":2})
if pass["verified"]!=true||fail["verified"]!=false {t.Fatal("wrong verification result")}
receipt:=value(t,h,"mirror.evidence",map[string]any{})
if len(receipt["sha256"].(string))!=64||receipt["credentials_exported"]!=false{t.Fatal(receipt)}
}
func TestFailClosedAndNoMutation(t *testing.T){
h,_:=New(goodToken)
bad:=[]struct{name string;args any}{
{"mirror.experiment",map[string]any{"fixture":"counter-v1","operation":"increment","request_id":"one","admit_synthetic":false}},
{"mirror.experiment",map[string]any{"fixture":"counter-v1","operation":"increment","request_id":"two","admit_synthetic":true,"unexpected":"x"}},
{"mirror.experiment",map[string]any{"fixture":"counter-v1","operation":"arbitrary","request_id":"three","admit_synthetic":true}},
{"mirror.environment",map[string]any{"prompt":"no"}},
{"mirror.ui_snapshot",map[string]any{"raw_data":true}},
{"mirror.ui_diff",map[string]any{"before":4,"after":8}},
{"unknown.tool",map[string]any{}},
}
for _,x:=range bad {r:=testCall(t,h,x.name,x.args);if r["error"]==nil||r["result"]!=nil{t.Fatalf("must refuse %s %+v",x.name,r)}}
if value(t,h,"mirror.ui_snapshot",map[string]any{})["counter"]!=float64(0){t.Fatal("mutation despite refusal")}
}
func TestBearerOriginAndBudget(t *testing.T){
h,_:=New(goodToken)
for _,x:=range []struct{auth,origin string;code int}{
{"","",401},{"Bearer wrong","",401},{"Bearer "+goodToken,"https://bad.invalid",403},{"Bearer "+goodToken,"",200}}{
r:=httptest.NewRequest("POST","/mcp",strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"tools/list"}`))
r.Header.Set("Content-Type","application/json");r.Header.Set("Authorization",x.auth);r.Header.Set("Origin",x.origin)
w:=httptest.NewRecorder();h.ServeHTTP(w,r);if w.Code!=x.code{t.Fatalf("got %d want %d",w.Code,x.code)}
}
for i:=0;i<8;i++{n:=string(rune('a'+i));value(t,h,"mirror.experiment",map[string]any{"fixture":"counter-v1","operation":"increment","request_id":n,"admit_synthetic":true})}
r:=testCall(t,h,"mirror.experiment",map[string]any{"fixture":"counter-v1","operation":"increment","request_id":"nine","admit_synthetic":true})
if r["error"]==nil{t.Fatal("budget unrestricted")}
}
