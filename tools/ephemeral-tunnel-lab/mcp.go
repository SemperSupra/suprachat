package tunnelexperiment

import (
 "crypto/rand"
 "crypto/sha256"
 "crypto/subtle"
 "encoding/hex"
 "encoding/json"
 "errors"
 "io"
 "net/http"
 "runtime"
 "strings"
 "time"
)

type Server struct { tokenDigest [32]byte; nonce string }
type request struct {
 JSONRPC string `json:"jsonrpc"`
 ID json.RawMessage `json:"id"`
 Method string `json:"method"`
 Params json.RawMessage `json:"params"`
}
func NewServer(token string) (*Server,error) {
 if len(token)<48 || len(token)>512 {return nil,errors.New("token must be 48-512 bytes")}
 b:=make([]byte,12)
 if _,err:=rand.Read(b);err!=nil{return nil,err}
 return &Server{tokenDigest:sha256.Sum256([]byte(token)),nonce:hex.EncodeToString(b)},nil
}
func jsonResponse(w http.ResponseWriter,status int,x any) {
 w.Header().Set("Content-Type","application/json")
 w.Header().Set("Cache-Control","no-store")
 w.Header().Set("X-Content-Type-Options","nosniff")
 w.Header().Set("Referrer-Policy","no-referrer")
 w.WriteHeader(status)
 _=json.NewEncoder(w).Encode(x)
}
func (s *Server) ServeHTTP(w http.ResponseWriter,r *http.Request) {
 // A public Quick Tunnel hostname is NEVER an authorization mechanism.
 // Do not leak whether any tools exist before bearer authentication.
 w.Header().Set("Cache-Control","no-store")
 w.Header().Set("X-Content-Type-Options","nosniff")
 a:=r.Header.Get("Authorization")
 if !strings.HasPrefix(a,"Bearer ") || len(a)>520 || subtle.ConstantTimeCompare(s.tokenDigest[:],digestToken(strings.TrimPrefix(a,"Bearer ")))!=1 {
  jsonResponse(w,http.StatusUnauthorized,map[string]string{"error":"unauthorized"});return
 }
 // Refuse browser origins; the experiment uses a non-browser stdlib client.
 if r.Header.Get("Origin")!="" {jsonResponse(w,http.StatusForbidden,map[string]string{"error":"browser origins not admitted"});return}
 if r.URL.Path=="/healthz" && r.Method=="GET" {
  jsonResponse(w,200,map[string]string{"status":"ready","scope":"synthetic_fixture_only"});return
 }
 if r.URL.Path!="/mcp" {jsonResponse(w,404,map[string]string{"error":"not found"});return}
 if r.Method!="POST" {w.Header().Set("Allow","POST");jsonResponse(w,405,map[string]string{"error":"method not allowed"});return}
 if !strings.HasPrefix(r.Header.Get("Content-Type"),"application/json") {jsonResponse(w,415,map[string]string{"error":"requires json"});return}
 r.Body=http.MaxBytesReader(w,r.Body,8192)
 defer r.Body.Close()
 b,err:=io.ReadAll(r.Body)
 if err!=nil {jsonResponse(w,413,map[string]string{"error":"bounded body required"});return}
 var q request
 if err=json.Unmarshal(b,&q);err!=nil || q.JSONRPC!="2.0" || q.Method=="" {
  jsonResponse(w,400,map[string]string{"error":"invalid jsonrpc request"});return
 }
 if q.Method=="notifications/initialized" && len(q.ID)==0 {w.WriteHeader(202);return}
 out:=map[string]any{"jsonrpc":"2.0","id":q.ID}
 switch q.Method {
 case "initialize":
  out["result"]=map[string]any{"protocolVersion":"2025-06-18","serverInfo":map[string]string{"name":"oec-ephemeral-quick-tunnel-lab","version":"0.1.0"},"capabilities":map[string]any{"tools":map[string]any{"listChanged":false}}}
 case "ping":
  out["result"]=map[string]any{}
 case "tools/list":
  out["result"]=map[string]any{"tools":[]any{
   map[string]any{"name":"lab.observe","description":"Direct runner-local environment observation; does not inspect ChatGPT internals","inputSchema":map[string]any{"type":"object","additionalProperties":false}},
   map[string]any{"name":"lab.digest","description":"SHA-256 of bounded synthetic challenge; no external side effects","inputSchema":map[string]any{"type":"object","properties":map[string]any{"challenge":map[string]string{"type":"string","minLength":"8","maxLength":"96"}},"required":[]string{"challenge"},"additionalProperties":false}},
  }}
 case "tools/call":
  var p struct {Name string `json:"name"`; Arguments json.RawMessage `json:"arguments"`}
  if err=json.Unmarshal(q.Params,&p);err!=nil {out["error"]=map[string]any{"code":-32602,"message":"invalid params"};break}
  var details map[string]string
  switch p.Name {
  case "lab.observe":
   if !emptyObject(p.Arguments) {out["error"]=map[string]any{"code":-32602,"message":"unexpected arguments"};break}
   details=map[string]string{"evidence_class":"DIRECT_RUNNER","os":runtime.GOOS,"architecture":runtime.GOARCH,"nonce":s.nonce,"transport":"HTTP_JSON_NONSTREAMING"}
  case "lab.digest":
   var arg map[string]string
   if err=json.Unmarshal(p.Arguments,&arg);err!=nil || len(arg)!=1 || len(arg["challenge"])<8 || len(arg["challenge"])>96 {
    out["error"]=map[string]any{"code":-32602,"message":"invalid challenge"};break
   }
   sum:=sha256.Sum256([]byte(arg["challenge"]))
   details=map[string]string{"evidence_class":"SYNTHETIC_REMOTE_ROUNDTRIP","sha256":hex.EncodeToString(sum[:]),"nonce":s.nonce}
  default:
   out["error"]=map[string]any{"code":-32601,"message":"unknown tool"}
  }
  if details!=nil {
   raw,_:=json.Marshal(details)
   out["result"]=map[string]any{"content":[]any{map[string]string{"type":"text","text":string(raw)}},"structuredContent":details,"isError":false}
  }
 default:
  out["error"]=map[string]any{"code":-32601,"message":"unsupported method"}
 }
 jsonResponse(w,200,out)
}
func digestToken(t string) []byte {s:=sha256.Sum256([]byte(t));return s[:]}
func emptyObject(raw json.RawMessage) bool {
 if len(raw)==0 || string(raw)=="null" {return true}
 var obj map[string]json.RawMessage
 return json.Unmarshal(raw,&obj)==nil && len(obj)==0
}
func Handler(token string) (http.Handler,error) {return NewServer(token)}
func HTTPServer(addr string,handler http.Handler) *http.Server {
 return &http.Server{Addr:addr,Handler:handler,ReadHeaderTimeout:4*time.Second,ReadTimeout:8*time.Second,WriteTimeout:8*time.Second,IdleTimeout:12*time.Second,MaxHeaderBytes:8<<10}
}
