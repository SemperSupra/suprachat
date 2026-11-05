package preflight

import (
 "bytes"
 "crypto/rand"
 "encoding/json"
 "fmt"
 "io"
 "net/http"
 "net/http/httptest"
 "strings"
 "sync"
 "sync/atomic"
 "testing"
 "time"
)
var syntheticKey = []byte("01234567890123456789012345678901")
var syntheticNow = time.Unix(1793830000, 0)
func create(t *testing.T) *Service {
 t.Helper()
 s, err := NewService(Config{RunID: "synthetic-run-a", Generation: "g-1", Secret: syntheticKey, Clock: func()time.Time{return syntheticNow}})
 if err!=nil{t.Fatal(err)}
 return s
}
var counter atomic.Uint64
func proof(scope string, issued, expires int64) Proof {
 return Proof{RunID:"synthetic-run-a",Generation:"g-1",Scope:scope,
  Nonce:fmt.Sprintf("nonce-%016d",counter.Add(1)),Issued:issued,Expires:expires}
}
func good(scope string) Proof{return proof(scope,syntheticNow.Unix()-1,syntheticNow.Unix()+60)}
func call(t *testing.T,c *http.Client, url,method,path string,body []byte,p Proof, mutate func(*http.Request)) (int,[]byte) {
 t.Helper()
 r,e:=http.NewRequest(method,url+path,bytes.NewReader(body));if e!=nil{t.Fatal(e)}
 p=Sign(syntheticKey,method,strings.Split(path,"?")[0],body,p)
 Attach(r,p)
 if method==http.MethodPost{r.Header.Set("Content-Type","application/json")}
 if mutate!=nil{mutate(r)}
 resp,e:=c.Do(r);if e!=nil{t.Fatal(e)}
 defer resp.Body.Close();b,e:=io.ReadAll(resp.Body);if e!=nil{t.Fatal(e)}
 return resp.StatusCode,b
}
func TestSyntheticEndToEndTLS(t *testing.T){
 s:=create(t);ts:=httptest.NewTLSServer(s.Handler());defer ts.Close()
 c:=ts.Client()
 status,b:=call(t,c,ts.URL,"GET","/v1/observe",nil,good("read"),nil)
 if status!=200{t.Fatalf("observation %d %q",status,b)}
 var before Observation;if e:=json.Unmarshal(b,&before);e!=nil{t.Fatal(e)}
 if before.AuthenticatedChatGPT || before.Revision!=0 || before.SyntheticFlag || before.EvidenceClass!=EvidenceClass {t.Fatalf("invalid synthetic evidence: %+v",before)}
 actionBody:=[]byte(`{"operation":"toggle-test-flag","expected_revision":0}`)
 status,b=call(t,c,ts.URL,"POST","/v1/synthetic/act",actionBody,good("synthetic-act"),nil)
 if status!=200 {t.Fatalf("synthetic action %d %q",status,b)}
 var receipt ActionReceipt;if e:=json.Unmarshal(b,&receipt);e!=nil{t.Fatal(e)}
 if receipt.AuthenticatedChatGPT || !receipt.Changed || receipt.Revision!=1 {t.Fatalf("incorrect action receipt: %+v",receipt)}
 status,b=call(t,c,ts.URL,"GET","/v1/observe",nil,good("read"),nil)
 var after Observation;_ = json.Unmarshal(b,&after)
 if status!=200||after.Revision!=1||!after.SyntheticFlag {t.Fatalf("no independent readback %d %+v",status,after)}
 s.Close()
 status,_=call(t,c,ts.URL,"GET","/v1/observe",nil,good("read"),nil)
 if status!=http.StatusForbidden{t.Fatalf("revocation failed %d",status)}
}
func TestAdversarialFailClosed(t *testing.T){
 tests:=[]struct{name string; method,path,scope string;body []byte; modifyProof func(*Proof);mutate func(*http.Request);want int}{
  {name:"incorrect_run",method:"GET",path:"/v1/observe",scope:"read",modifyProof:func(p *Proof){p.RunID="wrong-run"},want:403},
  {name:"incorrect_generation",method:"GET",path:"/v1/observe",scope:"read",modifyProof:func(p *Proof){p.Generation="other-generation"},want:403},
  {name:"incorrect_scope",method:"GET",path:"/v1/observe",scope:"synthetic-act",want:403},
  {name:"expired",method:"GET",path:"/v1/observe",scope:"read",modifyProof:func(p *Proof){p.Expires=syntheticNow.Unix()},want:403},
  {name:"future",method:"GET",path:"/v1/observe",scope:"read",modifyProof:func(p *Proof){p.Issued=syntheticNow.Unix()+1},want:403},
  {name:"excess_ttl",method:"GET",path:"/v1/observe",scope:"read",modifyProof:func(p *Proof){p.Expires=p.Issued+601},want:403},
  {name:"tampered_mac",method:"GET",path:"/v1/observe",scope:"read",mutate:func(r *http.Request){r.Header.Set("X-OEC-Proof",strings.Repeat("0",64))},want:403},
  {name:"browser_origin",method:"GET",path:"/v1/observe",scope:"read",mutate:func(r *http.Request){r.Header.Set("Origin","https://untrusted.example")},want:403},
  {name:"query_injection",method:"GET",path:"/v1/observe?token=fake",scope:"read",want:403},
  {name:"wrong_method",method:"PUT",path:"/v1/observe",scope:"read",want:404},
  {name:"unknown_endpoint",method:"GET",path:"/admin",scope:"read",want:404},
  {name:"missing_nonce",method:"GET",path:"/v1/observe",scope:"read",mutate:func(r *http.Request){r.Header.Del("X-OEC-Nonce")},want:403},
  {name:"duplicate_proof_header",method:"GET",path:"/v1/observe",scope:"read",mutate:func(r *http.Request){r.Header.Add("X-OEC-Proof","attacker-supplied-ambiguous-value")},want:403},
  {name:"duplicate_scope_header",method:"GET",path:"/v1/observe",scope:"read",mutate:func(r *http.Request){r.Header.Add("X-OEC-Scope","synthetic-act")},want:403},
  {name:"read_with_body",method:"GET",path:"/v1/observe",scope:"read",body:[]byte("fixture"),want:400},
  {name:"act_with_read_scope",method:"POST",path:"/v1/synthetic/act",scope:"read",body:[]byte(`{"operation":"toggle-test-flag","expected_revision":0}`),want:403},
  {name:"oversized_body",method:"POST",path:"/v1/synthetic/act",scope:"synthetic-act",body:bytes.Repeat([]byte("x"),MaxBody+1),want:413},
  {name:"duplicate_json_key",method:"POST",path:"/v1/synthetic/act",scope:"synthetic-act",body:[]byte(`{"operation":"toggle-test-flag","operation":"toggle-test-flag","expected_revision":0}`),want:400},
  {name:"unexpected_field",method:"POST",path:"/v1/synthetic/act",scope:"synthetic-act",body:[]byte(`{"operation":"toggle-test-flag","expected_revision":0,"prompt":"SECRET_CANARY"}`),want:400},
  {name:"malformed_json",method:"POST",path:"/v1/synthetic/act",scope:"synthetic-act",body:[]byte(`{"operation":`),want:400},
  {name:"incorrect_operation",method:"POST",path:"/v1/synthetic/act",scope:"synthetic-act",body:[]byte(`{"operation":"execute-shell","expected_revision":0}`),want:400},
  {name:"wrong_revision",method:"POST",path:"/v1/synthetic/act",scope:"synthetic-act",body:[]byte(`{"operation":"toggle-test-flag","expected_revision":1}`),want:409},
 }
 for _,tc:=range tests{t.Run(tc.name,func(t *testing.T){
  s:=create(t);ts:=httptest.NewServer(s.Handler());defer ts.Close()
  p:=good(tc.scope);if tc.modifyProof!=nil{tc.modifyProof(&p)}
  status,body:=call(t,ts.Client(),ts.URL,tc.method,tc.path,tc.body,p,tc.mutate)
  if status!=tc.want{t.Fatalf("expected %d got %d: %q",tc.want,status,body)}
  if bytes.Contains(body,[]byte("SECRET_CANARY"))||bytes.Contains(body,syntheticKey){t.Fatal("sensitive content echoed")}
  s.mu.Lock();rev:=s.revision;s.mu.Unlock();if rev!=0{t.Fatal("unapproved mutation")}
 })}
}
func TestTamperedBodyAndReplay(t *testing.T){
 s:=create(t);ts:=httptest.NewServer(s.Handler());defer ts.Close()
 p:=good("synthetic-act")
 body:=[]byte(`{"operation":"toggle-test-flag","expected_revision":0}`)
 status,_:=call(t,ts.Client(),ts.URL,"POST","/v1/synthetic/act",body,p,func(r *http.Request){
  different:=Sign(syntheticKey,"POST","/v1/synthetic/act",[]byte(`{"operation":"toggle-test-flag","expected_revision":1}`),p)
  r.Header.Set("X-OEC-Proof",different.MAC)
 })
 if status!=403{t.Fatalf("tampered body allowed: %d",status)}
 p=good("synthetic-act")
 status,_=call(t,ts.Client(),ts.URL,"POST","/v1/synthetic/act",body,p,nil)
 if status!=200{t.Fatal("valid action refused")}
 status,_=call(t,ts.Client(),ts.URL,"POST","/v1/synthetic/act",body,p,nil)
 if status!=403{t.Fatalf("replay allowed: %d",status)}
 status,_=call(t,ts.Client(),ts.URL,"POST","/v1/synthetic/act",body,good("synthetic-act"),nil)
 if status!=409{t.Fatalf("stale expected revision allowed: %d",status)}
}
func TestConcurrentReplayAndUniqueReaders(t *testing.T){
 s:=create(t);ts:=httptest.NewServer(s.Handler());defer ts.Close()
 var ok atomic.Int64
 p:=good("read")
 var wg sync.WaitGroup
 for i:=0;i<24;i++{wg.Add(1);go func(){defer wg.Done()
  r,_:=http.NewRequest("GET",ts.URL+"/v1/observe",nil)
  Attach(r,Sign(syntheticKey,"GET","/v1/observe",nil,p))
  resp,e:=ts.Client().Do(r);if e!=nil{return};defer resp.Body.Close()
  if resp.StatusCode==200{ok.Add(1)}
 }()};wg.Wait()
 if ok.Load()!=1{t.Fatalf("same nonce passed %d times",ok.Load())}
 for i:=0;i<5;i++ {status,_:=call(t,ts.Client(),ts.URL,"GET","/v1/observe",nil,good("read"),nil);if status!=200{t.Fatal("fresh read refused")}}
}
func TestRejectInvalidConfigAndProofLimit(t *testing.T){
 if _,e:=NewService(Config{RunID:"r",Generation:"g",Secret:[]byte("short"),Clock:time.Now});e==nil{t.Fatal("weak secret accepted")}
 if _,e:=NewService(Config{RunID:"r\nx",Generation:"g",Secret:syntheticKey,Clock:time.Now});e==nil{t.Fatal("newline run accepted")}
 s:=create(t);ts:=httptest.NewServer(s.Handler());defer ts.Close()
 random:=make([]byte,32);_,_ = rand.Read(random)
 status,reply:=call(t,ts.Client(),ts.URL,"GET","/v1/observe",nil,good("read"),func(r *http.Request){r.Header.Set("X-OEC-Proof",fmt.Sprintf("%x",random))})
 if status!=403 || bytes.Contains(reply,random){t.Fatal("random proof admitted or reflected")}
}

func TestOriginEvidenceOnSuccessAndRefusal(t *testing.T) {
 s:=create(t);ts:=httptest.NewServer(s.Handler());defer ts.Close()
 req,_:=http.NewRequest("GET",ts.URL+"/v1/observe",nil)
 unauth,err:=ts.Client().Do(req);if err!=nil{t.Fatal(err)}
 unauth.Body.Close()
 if unauth.StatusCode!=403 || unauth.Header.Get("X-OEC-Synthetic-Origin")!="v1" {t.Fatal("origin refusal marker missing")}
 req,_=http.NewRequest("GET",ts.URL+"/v1/observe",nil)
 p:=good("read")
 Attach(req,Sign(syntheticKey,"GET","/v1/observe",nil,p))
 allowed,err:=ts.Client().Do(req);if err!=nil{t.Fatal(err)}
 allowed.Body.Close()
 if allowed.StatusCode!=200 || allowed.Header.Get("X-OEC-Synthetic-Origin")!="v1" {t.Fatal("origin success marker missing")}
}
