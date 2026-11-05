// Command preflight performs a disposable loopback-only synthetic TLS session.
// It never reads, creates, or exports any real account/browser credentials.
package main

import (
 "bytes"
 "crypto/rand"
 "encoding/hex"
 "encoding/json"
 "errors"
 "fmt"
 "io"
 "net/http"
 "net/http/httptest"
 "os"
 "time"

 preflight "example.com/suprachat/oec-cloud-preflight"
)
type receipt struct {
 Schema string `json:"schema"`
 EvidenceClass string `json:"evidence_class"`
 Platform string `json:"platform"`
 SyntheticTLS bool `json:"synthetic_tls"`
 BeforeAfterReadback bool `json:"before_after_readback"`
 OneUseReplayRejected bool `json:"one_use_replay_rejected"`
 TeardownRevoked bool `json:"teardown_revoked"`
 SensitiveContentExported bool `json:"sensitive_content_exported"`
 AuthenticatedChatGPT bool `json:"authenticated_chatgpt"`
 LiveLocalHIL bool `json:"live_local_hil"`
}
func run() (receipt,error){
 output:=receipt{Schema:"suprachat-cloud-preflight-receipt/v1",EvidenceClass:preflight.EvidenceClass,
  Platform:os.Getenv("RUNNER_OS")}
 now:=time.Now().UTC()
 secret:=make([]byte,32);if _,err:=rand.Read(secret);err!=nil{return output,err}
 var random [12]byte;if _,err:=rand.Read(random[:]);err!=nil{return output,err}
 runID:="synthetic-"+hex.EncodeToString(random[:])
 cfg:=preflight.Config{RunID:runID,Generation:"synthetic-generation-1",Secret:secret,Clock:func()time.Time{return now}}
 svc,err:=preflight.NewService(cfg);if err!=nil{return output,err}
 ts:=httptest.NewTLSServer(svc.Handler());defer ts.Close()
 c:=ts.Client();c.Timeout=5*time.Second
 counter:=0
 submit:=func(method,path,scope string,body []byte,nonce string)(int,[]byte,error){
  counter++
  if nonce==""{nonce=fmt.Sprintf("synthetic-nonce-%016d",counter)}
  p:=preflight.Proof{RunID:runID,Generation:cfg.Generation,Scope:scope,Nonce:nonce,
   Issued:now.Unix()-1,Expires:now.Unix()+60}
  p=preflight.Sign(secret,method,path,body,p)
  req,e:=http.NewRequest(method,ts.URL+path,bytes.NewReader(body));if e!=nil{return 0,nil,e}
  preflight.Attach(req,p)
  if method==http.MethodPost{req.Header.Set("Content-Type","application/json")}
  resp,e:=c.Do(req);if e!=nil{return 0,nil,e}
  defer resp.Body.Close();b,e:=io.ReadAll(io.LimitReader(resp.Body,4096));return resp.StatusCode,b,e
 }
 st,b,e:=submit("GET","/v1/observe","read",nil,"");if e!=nil || st!=200{return output,errors.New("synthetic initial observation failed")}
 var before preflight.Observation;if json.Unmarshal(b,&before)!=nil || before.Revision!=0 || before.SyntheticFlag || before.AuthenticatedChatGPT {return output,errors.New("invalid initial observation")}
 action:=[]byte(`{"operation":"toggle-test-flag","expected_revision":0}`)
 st,b,e=submit("POST","/v1/synthetic/act","synthetic-act",action,"synthetic-action-1")
 var a preflight.ActionReceipt
 if e!=nil || st!=200 || json.Unmarshal(b,&a)!=nil || a.Revision!=1 || !a.Changed || a.AuthenticatedChatGPT{return output,errors.New("synthetic action not verified")}
 st,b,e=submit("GET","/v1/observe","read",nil,"")
 var after preflight.Observation
 if e!=nil || st!=200 || json.Unmarshal(b,&after)!=nil || after.Revision!=1 || !after.SyntheticFlag{return output,errors.New("independent readback failed")}
 output.BeforeAfterReadback=true
 st,_,e=submit("POST","/v1/synthetic/act","synthetic-act",action,"synthetic-action-1")
 if e!=nil || st!=403{return output,errors.New("replay rejection failed")}
 output.OneUseReplayRejected=true
 svc.Close()
 st,_,e=submit("GET","/v1/observe","read",nil,"")
 if e!=nil || st!=403{return output,errors.New("teardown revocation failed")}
 output.TeardownRevoked=true
 output.SyntheticTLS=true
 return output,nil
}
func main(){
 o,e:=run();if e!=nil{fmt.Fprintln(os.Stderr,"PREFLIGHT_SYNTHETIC_FAILURE:",e);os.Exit(1)}
 if err:=json.NewEncoder(os.Stdout).Encode(o);err!=nil{fmt.Fprintln(os.Stderr,"RECEIPT_WRITE_FAILED");os.Exit(1)}
}
