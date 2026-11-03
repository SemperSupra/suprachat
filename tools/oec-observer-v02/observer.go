package observer

import (
 "bytes"
 "crypto/rand"
 "crypto/sha256"
 "crypto/subtle"
 "encoding/hex"
 "encoding/json"
 "errors"
 "fmt"
 "io"
 "net/http"
 "regexp"
 "runtime"
 "sort"
 "strings"
 "sync"
 "time"
)

const Protocol = "2025-06-18"
const Evidence = "SYNTHETIC_MODEL_ONLY"
var idPattern = regexp.MustCompile(`^[a-z][a-z0-9-]{0,47}$`)

type Handler struct {
 auth [32]byte
 mu sync.Mutex
 counter int
 version int
 snapshots map[int]int
 operations map[string]int
 nonce string
}
type rpc struct {
 JSONRPC string `json:"jsonrpc"`
 ID json.RawMessage `json:"id"`
 Method string `json:"method"`
 Params json.RawMessage `json:"params"`
}
type invocation struct {
 Name string `json:"name"`
 Arguments json.RawMessage `json:"arguments"`
}
func New(token string) (*Handler,error) {
 if len(token)<48||len(token)>512 {return nil, errors.New("invalid bearer size")}
 nonce:=make([]byte,16)
 if _,e:=rand.Read(nonce);e!=nil{return nil,e}
 return &Handler{auth:sha256.Sum256([]byte(token)),version:1,snapshots:map[int]int{1:0},operations:map[string]int{},nonce:hex.EncodeToString(nonce)},nil
}
func (h *Handler) ServeHTTP(w http.ResponseWriter,r *http.Request) {
 w.Header().Set("Cache-Control","no-store")
 w.Header().Set("X-Content-Type-Options","nosniff")
 if r.Header.Get("Origin")!="" {reply(w,403,map[string]string{"error":"origin refused"});return}
 header:=r.Header.Get("Authorization")
 if !strings.HasPrefix(header,"Bearer ") {reply(w,401,map[string]string{"error":"unauthorized"});return}
 digest:=sha256.Sum256([]byte(strings.TrimPrefix(header,"Bearer ")))
 if subtle.ConstantTimeCompare(digest[:],h.auth[:])!=1 {reply(w,401,map[string]string{"error":"unauthorized"});return}
 if r.URL.Path!="/mcp" {reply(w,404,map[string]string{"error":"not found"});return}
 if r.Method!="POST" {reply(w,405,map[string]string{"error":"post required"});return}
 if !strings.HasPrefix(r.Header.Get("Content-Type"),"application/json") {reply(w,415,map[string]string{"error":"json required"});return}
 r.Body=http.MaxBytesReader(w,r.Body,16<<10)
 defer r.Body.Close()
 b,e:=io.ReadAll(r.Body);if e!=nil {reply(w,413,map[string]string{"error":"body limit"});return}
 var request rpc
 if e=decodeStrict(b,&request);e!=nil||request.JSONRPC!="2.0"||request.Method=="" {reply(w,400,map[string]string{"error":"invalid rpc envelope"});return}
 if request.Method=="notifications/initialized" && len(request.ID)==0 {w.WriteHeader(202);return}
 resp:=map[string]any{"jsonrpc":"2.0","id":request.ID}
 switch request.Method {
 case "initialize":
  resp["result"]=map[string]any{"protocolVersion":Protocol,"serverInfo":map[string]string{"name":"oec-mirror-observer-v02","version":"0.2.0"},"capabilities":map[string]any{"tools":map[string]any{"listChanged":false}}}
 case "ping":
  resp["result"]=map[string]any{}
 case "tools/list":
  tools:=[]any{}
  for _,name:=range toolNames() {
   tools=append(tools,map[string]any{"name":name,"description":"Authorized synthetic or runner-metadata observation only; never private ChatGPT internals","inputSchema":map[string]any{"type":"object","additionalProperties":true}})
  }
  resp["result"]=map[string]any{"tools":tools}
 case "tools/call":
  var call invocation
  if e=decodeStrict(request.Params,&call);e!=nil {resp["error"]=rpcError(-32602,"invalid call");break}
  v,err:=h.invoke(call.Name,call.Arguments)
  if err!=nil {resp["error"]=rpcError(-32602,err.Error());break}
  b,_:=json.Marshal(v)
  resp["result"]=map[string]any{"content":[]any{map[string]string{"type":"text","text":string(b)}},"structuredContent":v,"isError":false}
 default:
  resp["error"]=rpcError(-32601,"method not found")
 }
 reply(w,200,resp)
}
func rpcError(code int,msg string) any{return map[string]any{"code":code,"message":msg}}
func reply(w http.ResponseWriter,status int,obj any) {w.Header().Set("Content-Type","application/json");w.WriteHeader(status);_ =json.NewEncoder(w).Encode(obj)}
func decodeStrict(data []byte,v any) error {
 if len(data)==0 {return errors.New("missing args")}
 d:=json.NewDecoder(bytes.NewReader(data));d.DisallowUnknownFields()
 if e:=d.Decode(v);e!=nil{return e}
 var x any
 if d.Decode(&x)!=io.EOF{return errors.New("trailing data")}
 return nil
}
func toolNames() []string {
 return []string{"mirror.capabilities","mirror.environment","mirror.ui_snapshot","mirror.ui_diff","mirror.experiment","mirror.verify","mirror.evidence"}
}
func (h *Handler) invoke(name string,args json.RawMessage)(any,error) {
 h.mu.Lock();defer h.mu.Unlock()
 switch name {
 case "mirror.capabilities":
  if !empty(args){return nil,errors.New("arguments denied")}
  return map[string]any{"schema":"oec-observer-capabilities/v1","tools":toolNames(),"scope":"SYNTHETIC_FIXTURE_ONLY","native_browser":false,"chatgpt_internal_access":false,"evidence_class":Evidence},nil
 case "mirror.environment":
  if !empty(args){return nil,errors.New("arguments denied")}
  return map[string]any{"schema":"oec-observer-environment/v1","os":runtime.GOOS,"arch":runtime.GOARCH,"evidence_class":"DIRECT_RUNNER","session_nonce":h.nonce},nil
 case "mirror.ui_snapshot":
  if !empty(args){return nil,errors.New("arguments denied")}
  return h.snapshot(),nil
 case "mirror.ui_diff":
  var p struct{Before int `json:"before"`;After int `json:"after"`}
  if e:=decodeStrict(args,&p);e!=nil{return nil,errors.New("invalid diff bounds")}
  a,okA:=h.snapshots[p.Before];b,okB:=h.snapshots[p.After]
  if !okA||!okB||p.Before>p.After {return nil,errors.New("unknown or reversed version")}
  return map[string]any{"schema":"oec-observer-diff/v1","before":p.Before,"after":p.After,"counter_before":a,"counter_after":b,"changed":a!=b,"evidence_class":Evidence},nil
 case "mirror.experiment":
  var p struct {
   Fixture string `json:"fixture"`
   Operation string `json:"operation"`
   RequestID string `json:"request_id"`
   AdmitSynthetic bool `json:"admit_synthetic"`
  }
  if e:=decodeStrict(args,&p);e!=nil||!p.AdmitSynthetic||p.Fixture!="counter-v1"||p.Operation!="increment"||!idPattern.MatchString(p.RequestID) {
   return nil,errors.New("synthetic operation not admitted")
  }
  if v,ok:=h.operations[p.RequestID];ok {
   return map[string]any{"version":v,"counter":h.snapshots[v],"idempotent_replay":true,"evidence_class":Evidence},nil
  }
  if h.counter>=8 {return nil,errors.New("synthetic experiment budget exhausted")}
  h.counter++;h.version++
  h.snapshots[h.version]=h.counter
  h.operations[p.RequestID]=h.version
  return map[string]any{"version":h.version,"counter":h.counter,"idempotent_replay":false,"evidence_class":Evidence},nil
 case "mirror.verify":
  var p struct{ExpectedCounter int `json:"expected_counter"`;ExpectedVersion int `json:"expected_version"`}
  if e:=decodeStrict(args,&p);e!=nil{return nil,errors.New("invalid verify args")}
  return map[string]any{"verified":p.ExpectedCounter==h.counter&&p.ExpectedVersion==h.version,"actual_counter":h.counter,"actual_version":h.version,"evidence_class":Evidence},nil
 case "mirror.evidence":
  if !empty(args){return nil,errors.New("arguments denied")}
  raw:=[]byte(fmt.Sprintf("oec-observer-v02|counter-v1|%d|%d|%s",h.version,h.counter,h.nonce))
  sum:=sha256.Sum256(raw)
  return map[string]any{"schema":"oec-observer-receipt/v1","sha256":hex.EncodeToString(sum[:]),"counter":h.counter,"version":h.version,"evidence_class":Evidence,"prompts_exported":false,"credentials_exported":false},nil
 default:return nil,errors.New("unregistered tool")
 }
}
func (h *Handler) snapshot() any {
 return map[string]any{"schema":"oec-observer-snapshot/v1","fixture":"counter-v1","version":h.version,"counter":h.counter,"controls":[]any{map[string]string{"role":"button","name":"Increment","id":"increment"}},"evidence_class":Evidence,"real_browser":false}
}
func empty(args json.RawMessage)bool {
 if len(args)==0||string(args)=="null" {return true}
 var o map[string]json.RawMessage
 return json.Unmarshal(args,&o)==nil&&len(o)==0
}
func Listen(addr string,h *Handler)*http.Server {
 return &http.Server{Addr:addr,Handler:h,ReadHeaderTimeout:3*time.Second,ReadTimeout:7*time.Second,WriteTimeout:7*time.Second,IdleTimeout:10*time.Second,MaxHeaderBytes:8192}
}
func SortedNames()[]string{n:=toolNames();sort.Strings(n);return n}
