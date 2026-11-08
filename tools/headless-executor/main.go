// Command headless-executor is a bounded Agent Dispatch consumer of SupraChat's
// existing JSON-RPC stdio prompt interface. It is NOT a scheduler, owner-authority
// validator, browser automation layer, or credential broker.
//
// It submits exactly one admitted prompt to locally installed SupraChat.Automation
// and emits one JSON receipt on stdout. It neither retries nor stores credentials.
package main

import (
 "bufio"
 "bytes"
 "context"
 "crypto/rand"
 "encoding/hex"
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "os/exec"
 "regexp"
 "strings"
 "time"
)

const (
 requestSchema = "suprachat-executor-request/v1"
 receiptSchema = "suprachat-executor-receipt/v1"
 maxRequestBytes = 128 * 1024
 maxOutputLine = 4 * 1024 * 1024
)
var idPattern=regexp.MustCompile(`^[A-Za-z0-9_.:-]{8,128}$`)
var modelPattern=regexp.MustCompile(`^[A-Za-z0-9_./:-]{1,120}$`)
var revisionPattern=regexp.MustCompile(`^[a-f0-9]{40}$`)

type Request struct {
 Schema string `json:"schema"`
 OperationID string `json:"operation_id"`
 AssignmentID string `json:"assignment_id"`
 AuthorityRevision string `json:"authority_revision"`
 Model string `json:"model"`
 Prompt string `json:"prompt"`
 RequireJSONObject bool `json:"require_json_object,omitempty"`
}
type Receipt struct {
 Schema string `json:"schema"`
 OperationID string `json:"operation_id"`
 AssignmentID string `json:"assignment_id"`
 AuthorityRevision string `json:"authority_revision"`
 Status string `json:"status"`
 ErrorCode string `json:"error_code,omitempty"`
 ProviderRequestID string `json:"provider_request_id,omitempty"`
 Binding string `json:"binding,omitempty"`
 OutputText string `json:"output_text,omitempty"`
 ParsedObject json.RawMessage `json:"parsed_object,omitempty"`
 ProviderCompleted bool `json:"provider_completed"`
 OwnerEffectAccepted bool `json:"owner_effect_accepted"`
}
type RPCResult struct {
 Schema string `json:"schema"`
 Binding string `json:"binding"`
 Completed bool `json:"completed"`
 RequestID string `json:"request_id"`
 OutputText string `json:"output_text"`
}
type RPCError struct {
 Code int `json:"code"`
 Type string `json:"type"`
 Message string `json:"message"`
}
type RPCLine struct {
 JSONRPC string `json:"jsonrpc"`
 ID json.RawMessage `json:"id"`
 Method string `json:"method"`
 Result json.RawMessage `json:"result"`
 Error *RPCError `json:"error"`
}
func decodeOne(raw []byte, into any) error {
 d:=json.NewDecoder(bytes.NewReader(raw))
 d.DisallowUnknownFields()
 if err:=d.Decode(into);err!=nil{return err}
 var other any
 if err:=d.Decode(&other);err!=io.EOF{return errors.New("multiple JSON objects")}
 return nil
}
func validate(req Request) error {
 if req.Schema!=requestSchema||!idPattern.MatchString(req.OperationID)||
    !idPattern.MatchString(req.AssignmentID)||!revisionPattern.MatchString(req.AuthorityRevision)||
    !modelPattern.MatchString(req.Model)||strings.TrimSpace(req.Prompt)==""||
    len(req.Prompt)>65536{return errors.New("invalid or unsupported request")}
 return nil
}
func emptyReceipt(r Request) Receipt {
 return Receipt{Schema:receiptSchema,OperationID:r.OperationID,
    AssignmentID:r.AssignmentID,AuthorityRevision:r.AuthorityRevision,
    Status:"UNKNOWN",OwnerEffectAccepted:false,ProviderCompleted:false}
}
func newRPCID() (string,error) {
 b:=make([]byte,16)
 if _,err:=rand.Read(b);err!=nil{return "",err}
 return hex.EncodeToString(b),nil
}
// evaluate is deliberately strict about completion, correlation, and valid
// output. A complete model response never claims a verified owner effect.
func evaluate(r Request, rpcID string, lines [][]byte, processErr error) Receipt {
 out:=emptyReceipt(r)
 if processErr!=nil {
  out.ErrorCode="PROCESS_UNCERTAIN"
  return out
 }
 if len(lines)==0 {
  out.ErrorCode="NO_RESPONSE"
  return out
 }
 found:=false
 for _,raw:=range lines {
  var line RPCLine
  if err:=json.Unmarshal(raw,&line);err!=nil||line.JSONRPC!="2.0"{
   bad:=emptyReceipt(r)
   bad.ErrorCode="MALFORMED_RPC"
   return bad
  }
  if line.Method!=""&&len(line.ID)==0 {continue} // allowed JSON-RPC notification
  var id string
  if err:=json.Unmarshal(line.ID,&id);err!=nil||id!=rpcID {
   bad:=emptyReceipt(r)
   bad.ErrorCode="CORRELATION_MISMATCH"
   return bad
  }
  if found {
   bad:=emptyReceipt(r)
   bad.ErrorCode="DUPLICATE_REPLY"
   return bad
  }
  found=true
  if line.Error!=nil {
   switch line.Error.Type {
   case "AUTH_REQUIRED","INVALID_PARAMS","USAGE","METHOD_NOT_FOUND","BROKER_UNAVAILABLE","SCOPE_DENIED":
    out.Status="REFUSED"
    out.ErrorCode=line.Error.Type
   case "INCOMPLETE":
    out.ErrorCode="INCOMPLETE"
   default:
    out.ErrorCode="RPC_ERROR"
   }
   continue
  }
  var v RPCResult
  if len(line.Result)==0||json.Unmarshal(line.Result,&v)!=nil {
   out.ErrorCode="INVALID_RPC_RESULT"
   continue
  }
  if v.Schema!="suprachat-machine/v1"||v.Binding!="siwc-responses" {
   out.ErrorCode="UNEXPECTED_PROVIDER_BINDING"
   continue
  }
  if !v.Completed||strings.TrimSpace(v.RequestID)=="" {
   out.ErrorCode="PROVIDER_INCOMPLETE"
   continue
  }
  out.ProviderCompleted=true
  out.ProviderRequestID=v.RequestID
  out.Binding=v.Binding
  if strings.TrimSpace(v.OutputText)==""{
   out.Status="INVALID_RESULT"
   out.ErrorCode="EMPTY_MODEL_RESPONSE"
   continue
  }
  out.OutputText=v.OutputText
  if r.RequireJSONObject {
   var parsed map[string]json.RawMessage
   if err:=decodeOne([]byte(v.OutputText),&parsed);err!=nil||parsed==nil {
    out.Status="INVALID_RESULT"
    out.ErrorCode="RESULT_NOT_JSON_OBJECT"
    continue
   }
   out.ParsedObject=json.RawMessage(v.OutputText)
  }
  out.Status="COMPLETED"
 }
 if !found {
  out.ErrorCode="NO_MATCHING_REPLY"
 }
 return out
}
// runOnce executes no shell. Provider credentials remain owned by the local
// SupraChat process. stderr is discarded to avoid leaking provider diagnostics.
func runOnce(ctx context.Context, req Request, binary string) Receipt {
 out:=emptyReceipt(req)
 if err:=validate(req);err!=nil {
  out.Status="REFUSED";out.ErrorCode="INVALID_REQUEST";return out
 }
 rpcID,err:=newRPCID()
 if err!=nil {out.ErrorCode="CORRELATION_UNAVAILABLE";return out}
 cmd:=exec.CommandContext(ctx,binary,"stdio")
 cmd.Stderr=io.Discard
 stdin,err:=cmd.StdinPipe()
 if err!=nil{out.ErrorCode="PROCESS_UNAVAILABLE";return out}
 stdout,err:=cmd.StdoutPipe()
 if err!=nil{out.ErrorCode="PROCESS_UNAVAILABLE";return out}
 if err:=cmd.Start();err!=nil{out.ErrorCode="PROCESS_UNAVAILABLE";return out}
 payload,_:=json.Marshal(map[string]any{"jsonrpc":"2.0","id":rpcID,"method":"responses/create",
   "params":map[string]any{"model":req.Model,"input":req.Prompt}})
 _,writeErr:=stdin.Write(append(payload,'\n'))
 closeErr:=stdin.Close()
 if writeErr!=nil||closeErr!=nil {
  _=cmd.Process.Kill();_=cmd.Wait()
  out.ErrorCode="SEND_UNCERTAIN";return out
 }
 lines:=make([][]byte,0,3)
 scanner:=bufio.NewScanner(stdout)
 scanner.Buffer(make([]byte,64*1024),maxOutputLine)
 scanErr:=error(nil)
 for scanner.Scan() {
  if len(lines)>=128{scanErr=errors.New("too many output frames");break}
  lines=append(lines,append([]byte(nil),scanner.Bytes()...))
 }
 if err:=scanner.Err();err!=nil{scanErr=err}
 if scanErr!=nil {_=cmd.Process.Kill()}
 processErr:=cmd.Wait()
 if ctx.Err()!=nil{out.ErrorCode="TIMEOUT_OR_CANCELLED";return out}
 if scanErr!=nil{out.ErrorCode="OUTPUT_UNCERTAIN";return out}
 return evaluate(req,rpcID,lines,processErr)
}
func main() {
 executable:=flag.String("executable","SupraChat.Automation","trusted local executable path; fixed stdio argument")
 deadline:=flag.Duration("timeout",2*time.Minute,"single invocation deadline (max 10m)")
 flag.Parse()
 if *deadline<=0||*deadline>10*time.Minute||flag.NArg()!=0 {
  fmt.Fprintln(os.Stderr,"invalid local configuration")
  os.Exit(2)
 }
 input,err:=io.ReadAll(io.LimitReader(os.Stdin,maxRequestBytes+1))
 if err!=nil||len(input)>maxRequestBytes {
  fmt.Fprintln(os.Stderr,"invalid request envelope")
  os.Exit(2)
 }
 var req Request
 if err:=decodeOne(input,&req);err!=nil {
  fmt.Fprintln(os.Stderr,"invalid request JSON")
  os.Exit(2)
 }
 ctx,cancel:=context.WithTimeout(context.Background(),*deadline)
 defer cancel()
 receipt:=runOnce(ctx,req,*executable)
 encoder:=json.NewEncoder(os.Stdout)
 encoder.SetEscapeHTML(true)
 if encoder.Encode(receipt)!=nil{os.Exit(3)}
 if receipt.Status!="COMPLETED"{os.Exit(1)}
}

