// Package preflight qualifies a deliberately synthetic, least-authority local
// observation contract. It does not connect to ChatGPT, a browser profile,
// a private host, or any user authentication state.
package preflight

import (
 "bytes"
 "crypto/hmac"
 "crypto/sha256"
 "encoding/hex"
 "encoding/json"
 "errors"
 "io"
 "net/http"
 "strconv"
 "strings"
 "sync"
 "time"
)

const (
 Schema = "suprachat-cloud-preflight/v1"
 EvidenceClass = "SYNTHETIC_ONLY_NOT_AUTHENTICATED_CHATGPT"
 MaxBody = 256
 MaxTTL = 600 * time.Second
)
type Config struct {
 RunID string
 Generation string
 Secret []byte
 Clock func() time.Time
}
type Service struct {
 mu sync.Mutex
 cfg Config
 seen map[string]struct{}
 revision uint64
 flag bool
 closed bool
}
type Observation struct {
 Schema string `json:"schema"`
 EvidenceClass string `json:"evidence_class"`
 AuthenticatedChatGPT bool `json:"authenticated_chatgpt"`
 Revision uint64 `json:"revision"`
 SyntheticFlag bool `json:"synthetic_flag"`
}
type ActionReceipt struct {
 Schema string `json:"schema"`
 EvidenceClass string `json:"evidence_class"`
 AuthenticatedChatGPT bool `json:"authenticated_chatgpt"`
 Changed bool `json:"changed"`
 Revision uint64 `json:"revision"`
}
func NewService(cfg Config) (*Service, error) {
 if cfg.RunID == "" || cfg.Generation == "" || len(cfg.Secret) < 32 || cfg.Clock == nil ||
    strings.ContainsAny(cfg.RunID, "\n\r") || strings.ContainsAny(cfg.Generation, "\n\r") {
   return nil, errors.New("missing or unsafe synthetic binding")
 }
 secret := make([]byte,len(cfg.Secret)); copy(secret,cfg.Secret);cfg.Secret=secret
 return &Service{cfg:cfg, seen:make(map[string]struct{})},nil
}
func (s *Service) Close() {s.mu.Lock();defer s.mu.Unlock();s.closed=true;clear(s.seen)}
// Signature binds source-run, session generation, method, fixed path, explicit
// capability, body hash, one-use nonce and short validity window.
func Signature(secret []byte, method,path,run,generation,scope,nonce string, issued,expires int64, body []byte) string {
 digest:=sha256.Sum256(body)
 canonical:=strings.Join([]string{method,path,run,generation,scope,nonce,
  strconv.FormatInt(issued,10),strconv.FormatInt(expires,10),hex.EncodeToString(digest[:])},"\n")
 mac:=hmac.New(sha256.New,secret);_,_=mac.Write([]byte(canonical));return hex.EncodeToString(mac.Sum(nil))
}
type Proof struct {
 RunID string
 Generation string
 Scope string
 Nonce string
 Issued int64
 Expires int64
 MAC string
}
func Sign(secret []byte, method,path string, body []byte, p Proof) Proof {
 p.MAC=Signature(secret,method,path,p.RunID,p.Generation,p.Scope,p.Nonce,p.Issued,p.Expires,body)
 return p
}
func Attach(r *http.Request,p Proof) {
 h:=r.Header
 h.Set("X-OEC-Run",p.RunID);h.Set("X-OEC-Generation",p.Generation)
 h.Set("X-OEC-Scope",p.Scope);h.Set("X-OEC-Nonce",p.Nonce)
 h.Set("X-OEC-Issued",strconv.FormatInt(p.Issued,10))
 h.Set("X-OEC-Expires",strconv.FormatInt(p.Expires,10));h.Set("X-OEC-Proof",p.MAC)
}
func (s *Service) Handler() http.Handler {return http.HandlerFunc(s.serve)}
func deny(w http.ResponseWriter,status int){w.Header().Set("Cache-Control","no-store");http.Error(w,http.StatusText(status),status)}
func jsonResponse(w http.ResponseWriter,status int,value any){
 w.Header().Set("Content-Type","application/json");w.Header().Set("Cache-Control","no-store")
 w.Header().Set("X-Content-Type-Options","nosniff");w.WriteHeader(status);_=json.NewEncoder(w).Encode(value)
}
func (s *Service) serve(w http.ResponseWriter,r *http.Request){
 w.Header().Set("Cache-Control","no-store")
 // Fixed nonsecret origin evidence prevents mistaking a tunnel edge error for a service refusal.
 w.Header().Set("X-OEC-Synthetic-Origin","v1")
 if r.Header.Get("Origin")!="" {deny(w,http.StatusForbidden);return}
 var scope string
 switch {
 case r.URL.Path=="/v1/observe" && r.Method==http.MethodGet:scope="read"
 case r.URL.Path=="/v1/synthetic/act" && r.Method==http.MethodPost:scope="synthetic-act"
 default:deny(w,http.StatusNotFound);return
 }
 if r.URL.RawQuery!="" || r.URL.User!=nil {deny(w,http.StatusForbidden);return}
 body,err:=io.ReadAll(io.LimitReader(r.Body,MaxBody+1));if err!=nil || len(body)>MaxBody {deny(w,http.StatusRequestEntityTooLarge);return}
 if scope=="read" && len(body)!=0 {deny(w,http.StatusBadRequest);return}
 if scope=="synthetic-act" && r.Header.Get("Content-Type")!="application/json" {deny(w,http.StatusUnsupportedMediaType);return}
 if !s.authorize(r,scope,body){deny(w,http.StatusForbidden);return}
 if scope=="read" {
  s.mu.Lock();closed:=s.closed; rev:=s.revision;flag:=s.flag;s.mu.Unlock()
  if closed {deny(w,http.StatusServiceUnavailable);return}
  jsonResponse(w,http.StatusOK,Observation{Schema,EvidenceClass,false,rev,flag});return
 }
 req,err:=decodeAction(body)
 if err!=nil {deny(w,http.StatusBadRequest);return}
 s.mu.Lock();defer s.mu.Unlock()
 if s.closed {deny(w,http.StatusServiceUnavailable);return}
 if req.ExpectedRevision!=s.revision {deny(w,http.StatusConflict);return}
 s.flag=!s.flag;s.revision++
 jsonResponse(w,http.StatusOK,ActionReceipt{Schema,EvidenceClass,false,true,s.revision})
}
func (s *Service) authorize(r *http.Request,requiredScope string,body []byte) bool {
 h:=r.Header
 // Duplicate security headers are ambiguous across proxies and must fail closed.
 for _,name:=range []string{"X-OEC-Run","X-OEC-Generation","X-OEC-Scope","X-OEC-Nonce","X-OEC-Issued","X-OEC-Expires","X-OEC-Proof"}{
  if len(h.Values(name))!=1{return false}
 }
 nonce:=h.Get("X-OEC-Nonce")
 if len(nonce)<16 || len(nonce)>96 || strings.ContainsAny(nonce,"\r\n") ||
   h.Get("X-OEC-Run")!=s.cfg.RunID ||h.Get("X-OEC-Generation")!=s.cfg.Generation ||
   h.Get("X-OEC-Scope")!=requiredScope {return false}
 issued,e1:=strconv.ParseInt(h.Get("X-OEC-Issued"),10,64)
 expires,e2:=strconv.ParseInt(h.Get("X-OEC-Expires"),10,64)
 now:=s.cfg.Clock().Unix()
 if e1!=nil || e2!=nil || issued>now || expires<=now || expires<=issued ||
     expires-issued>int64(MaxTTL.Seconds()) {return false}
 expected:=Signature(s.cfg.Secret,r.Method,r.URL.Path,s.cfg.RunID,s.cfg.Generation,requiredScope,nonce,issued,expires,body)
 got,err:=hex.DecodeString(h.Get("X-OEC-Proof"))
 want,_:=hex.DecodeString(expected)
 if err!=nil || len(got)!=len(want) || !hmac.Equal(got,want) {return false}
 s.mu.Lock();defer s.mu.Unlock()
 if s.closed {return false}
 if _,exists:=s.seen[nonce];exists {return false}
 // Bounded synthetic run: fail closed rather than allow unbounded nonce storage.
 if len(s.seen)>=2048 {return false}
 s.seen[nonce]=struct{}{}
 return true
}
type action struct {Operation string;ExpectedRevision uint64}
func decodeAction(data []byte)(action,error){
 var a action
 d:=json.NewDecoder(bytes.NewReader(data));d.UseNumber()
 t,e:=d.Token();if e!=nil || t!=json.Delim('{') {return a,errors.New("not an object")}
 keys:=make(map[string]bool)
 for d.More(){
  tk,e:=d.Token();if e!=nil{return a,e};key,ok:=tk.(string);if !ok || keys[key] {return a,errors.New("duplicate or invalid key")};keys[key]=true
  switch key {
  case "operation":e=d.Decode(&a.Operation)
  case "expected_revision":e=d.Decode(&a.ExpectedRevision)
  default:return a,errors.New("unknown key")
  };if e!=nil{return a,e}
 }
 if _,e=d.Token();e!=nil{return a,e}
 if _,e=d.Token();e!=io.EOF {return a,errors.New("trailing JSON")}
 if len(keys)!=2 || a.Operation!="toggle-test-flag" {return a,errors.New("invalid operation")}
 return a,nil
}
