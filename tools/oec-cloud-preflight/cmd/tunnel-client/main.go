// Command tunnel-client verifies real public HTTPS against the synthetic-only
// loopback service. No account state, profiles, prompts or user URLs are used.
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
 "os"
 "regexp"
 "time"
 preflight "example.com/suprachat/oec-cloud-preflight"
)
type receipt struct {
 Schema string `json:"schema"`
 EvidenceClass string `json:"evidence_class"`
 RealCloudflareHTTPS bool `json:"real_cloudflare_https"`
 UnauthorizedDenied bool `json:"unauthorized_denied"`
 ForeignOriginDenied bool `json:"foreign_origin_denied"`
 SyntheticActionReadback bool `json:"synthetic_action_readback"`
 ReplayDenied bool `json:"replay_denied"`
 WrongRunDenied bool `json:"wrong_run_denied"`
 AuthenticatedChatGPT bool `json:"authenticated_chatgpt"`
 UserCredentialsTransferred bool `json:"user_credentials_transferred"`
}
func probe() (receipt,error) {
 output:=receipt{Schema:"suprachat-cloudflare-preflight/v1",EvidenceClass:preflight.EvidenceClass}
 url:=os.Getenv("OEC_ENDPOINT")
 if !regexp.MustCompile(`^https://[A-Za-z0-9-]+\.trycloudflare\.com$`).MatchString(url){
  return output,errors.New("unsafe endpoint")
 }
 key,err:=hex.DecodeString(os.Getenv("OEC_SECRET_HEX"))
 if err!=nil || len(key)!=32 || os.Getenv("OEC_RUN_ID")==""{return output,errors.New("binding missing")}
 runID:=os.Getenv("OEC_RUN_ID")
 client:=&http.Client{Timeout:15*time.Second,CheckRedirect:func(*http.Request,[]*http.Request)error{return errors.New("redirect forbidden")}}
 now:=time.Now()
 invoke:=func(method,path,scope,nonce,run string,body []byte,auth bool,origin bool)(int,[]byte,error){
  req,e:=http.NewRequest(method,url+path,bytes.NewReader(body));if e!=nil{return 0,nil,e}
  req.Header.Set("Cache-Control","no-store")
  if method=="POST"{req.Header.Set("Content-Type","application/json")}
  if origin{req.Header.Set("Origin","https://untrusted.invalid")}
  if auth{
   p:=preflight.Proof{RunID:run,Generation:"synthetic-g1",Scope:scope,Nonce:nonce,
    Issued:now.Unix()-1,Expires:now.Unix()+120}
   preflight.Attach(req,preflight.Sign(key,method,path,body,p))
  }
  resp,e:=client.Do(req);if e!=nil{return 0,nil,errors.New("remote HTTPS request unavailable")}
  defer resp.Body.Close()
  if resp.Header.Get("X-OEC-Synthetic-Origin")!="v1"{return 0,nil,errors.New("remote origin provenance missing")}
  answer,e:=io.ReadAll(io.LimitReader(resp.Body,2048))
  return resp.StatusCode,answer,e
 }
 nonce:=func()string{var b [12]byte;if _,e:=rand.Read(b[:]);e!=nil{return ""};return hex.EncodeToString(b[:])}
 // A Quick Tunnel hostname may appear before the Cloudflare edge route is
 // fully propagated. Probe only the fixed unauthorized endpoint until it
 // demonstrably returns the local service's 403, never accept a 2xx result.
 status:=0
 ready:=false
 for attempt:=0;attempt<24;attempt++{
  var e error
  status,_,e=invoke("GET","/v1/observe","read",nonce(),runID,nil,false,false)
  if e==nil && status==403{ready=true;break}
  if e==nil && status>=200 && status<300{return output,errors.New("remote unauthorized request accepted")}
  time.Sleep(2*time.Second)
 }
 if !ready{return output,fmt.Errorf("remote unauthorized proof absent status=%d",status)}
 output.UnauthorizedDenied=true
 status,_,err=invoke("GET","/v1/observe","read",nonce(),runID,nil,true,true)
 if err!=nil||status!=403{return output,errors.New("foreign origin not refused")}
 output.ForeignOriginDenied=true
 status,b,err:=invoke("GET","/v1/observe","read",nonce(),runID,nil,true,false)
 var before preflight.Observation
 if err!=nil||status!=200||json.Unmarshal(b,&before)!=nil||before.Revision!=0||before.SyntheticFlag||before.AuthenticatedChatGPT{return output,errors.New("initial remote state invalid")}
 body:=[]byte(`{"operation":"toggle-test-flag","expected_revision":0}`)
 replayNonce:=nonce()
 status,b,err=invoke("POST","/v1/synthetic/act","synthetic-act",replayNonce,runID,body,true,false)
 var a preflight.ActionReceipt
 if err!=nil||status!=200||json.Unmarshal(b,&a)!=nil||!a.Changed||a.Revision!=1||a.AuthenticatedChatGPT{return output,errors.New("remote synthetic action invalid")}
 status,b,err=invoke("GET","/v1/observe","read",nonce(),runID,nil,true,false)
 var after preflight.Observation
 if err!=nil||status!=200||json.Unmarshal(b,&after)!=nil||!after.SyntheticFlag||after.Revision!=1{return output,errors.New("independent remote state readback failed")}
 output.SyntheticActionReadback=true
 status,_,err=invoke("POST","/v1/synthetic/act","synthetic-act",replayNonce,runID,body,true,false)
 if err!=nil||status!=403{return output,errors.New("remote replay not rejected")}
 output.ReplayDenied=true
 status,_,err=invoke("GET","/v1/observe","read",nonce(),"wrong-synthetic-run",nil,true,false)
 if err!=nil||status!=403{return output,errors.New("cross-run auth not rejected")}
 output.WrongRunDenied=true
 output.RealCloudflareHTTPS=true
 return output,nil
}
func main(){
 value,err:=probe()
 if err!=nil{fmt.Fprintln(os.Stderr,"CLOUDFLARE_SYNTHETIC_PREFLIGHT_FAIL:",err);os.Exit(1)}
 blob,err:=json.Marshal(value)
 if err!=nil{os.Exit(1)}
 if err=os.MkdirAll("evidence",0700);err!=nil{os.Exit(1)}
 if err=os.WriteFile("evidence/cloudflare-preflight.json",append(blob,'\n'),0600);err!=nil{os.Exit(1)}
 fmt.Println("QUALIFIED_REAL_CLOUDFLARE_SYNTHETIC_SCOPED_HTTPS auth=false browser=false replay=refused")
}
