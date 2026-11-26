package main

import (
 "bufio"
 "context"
 "encoding/json"
 "fmt"
 "os"
 "strings"
 "testing"
 "time"
)

const rev="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
func syntheticRequest() Request {
 return Request{Schema:requestSchema,OperationID:"op-12345678",AssignmentID:"assignment-12345",
 AuthorityRevision:rev,ProviderBinding:"supported_responses",Model:"synthetic-model",Prompt:"Synthetic prompt: return only JSON."}
}
// TestMain provides a credential-free subprocess speaking the exact public
// SupraChat.Automation JSON-RPC response shape. It is NOT a model call.
func TestMain(m *testing.M) {
 if mode:=os.Getenv("SUPRA_STUB_MODE");mode!="" {
  reader:=bufio.NewScanner(os.Stdin)
  if !reader.Scan(){os.Exit(3)}
  var in struct {
   ID string `json:"id"`
   Method string `json:"method"`
   Params struct { Model string `json:"model"`; Input string `json:"input"` } `json:"params"`
  }
  if json.Unmarshal(reader.Bytes(),&in)!=nil||in.Method!="responses/create"||in.Params.Model!="synthetic-model"||!strings.Contains(in.Params.Input,"Synthetic prompt"){
   os.Exit(4)
  }
  response:=func(completed bool,output string) string{
   b,_:=json.Marshal(map[string]any{
    "jsonrpc":"2.0","id":in.ID,
    "result":map[string]any{"schema":"suprachat-machine/v1","binding":"siwc-responses",
      "completed":completed,"request_id":"synthetic-request-id","output_text":output},
   })
   return string(b)
  }
  switch mode {
  case "complete":
   fmt.Println(response(true,"Synthetic result for later processing"))
  case "json":
   fmt.Println(response(true,`{"decision":"READY","evidence":"synthetic"}`))
  case "not-json":
   fmt.Println(response(true,"Not JSON."))
  case "incomplete":
   fmt.Println(response(false,"partial text must not be trusted"))
  case "refused":
   b,_:=json.Marshal(map[string]any{"jsonrpc":"2.0","id":in.ID,
      "error":map[string]any{"code":-32000,"type":"AUTH_REQUIRED","message":"synthetic"}})
   fmt.Println(string(b))
  case "wrong-id":
   fmt.Println(`{"jsonrpc":"2.0","id":"wrong-id","result":{}}`)
  case "duplicate":
   fmt.Println(response(true,"first"))
   fmt.Println(response(true,"second"))
  case "notification":
   fmt.Println(`{"jsonrpc":"2.0","method":"codex/event","params":{"kind":"synthetic"}}`)
   fmt.Println(response(true,"from same id"))
  case "malformed":
   fmt.Println("unstructured text")
  case "exit-error":
   fmt.Println(response(true,"should not accept failed process"))
   os.Exit(4)
  case "silence":
  case "timeout":
   time.Sleep(5*time.Second)
  default:
   os.Exit(5)
  }
  os.Exit(0)
 }
 os.Exit(m.Run())
}
func runStub(t *testing.T,mode string,req Request,limit time.Duration) Receipt {
 t.Helper()
 t.Setenv("SUPRA_STUB_MODE",mode)
 ctx,cancel:=context.WithTimeout(context.Background(),limit)
 defer cancel()
 return runOnce(ctx,req,os.Args[0])
}
func TestValidCompleteResponse(t *testing.T){
 r:=runStub(t,"complete",syntheticRequest(),5*time.Second)
 if r.Status!="COMPLETED"||r.ProviderRequestID!="synthetic-request-id"||
    r.OutputText!="Synthetic result for later processing"||!r.ProviderCompleted||
    r.OwnerEffectAccepted||r.AssignmentID!="assignment-12345"{
  t.Fatalf("invalid complete receipt: %+v",r)
 }
}
func TestStructuredDownstreamResult(t *testing.T){
 req:=syntheticRequest();req.RequireJSONObject=true
 r:=runStub(t,"json",req,5*time.Second)
 if r.Status!="COMPLETED"||!strings.Contains(string(r.ParsedObject),`"decision":"READY"`){t.Fatalf("invalid structured receipt: %+v",r)}
}
func TestInvalidJSONNotDownstreamAccepted(t *testing.T){
 req:=syntheticRequest();req.RequireJSONObject=true
 r:=runStub(t,"not-json",req,5*time.Second)
 if r.Status!="INVALID_RESULT"||r.ErrorCode!="RESULT_NOT_JSON_OBJECT"||r.OwnerEffectAccepted {t.Fatalf("unsafe parsed result: %+v",r)}
}
func TestIncompleteCannotSucceed(t *testing.T){
 r:=runStub(t,"incomplete",syntheticRequest(),5*time.Second)
 if r.Status!="UNKNOWN"||r.ErrorCode!="PROVIDER_INCOMPLETE"||r.OutputText!=""{t.Fatalf("unsafe incomplete result: %+v",r)}
}
func TestMissingAuthRefused(t *testing.T){
 r:=runStub(t,"refused",syntheticRequest(),5*time.Second)
 if r.Status!="REFUSED"||r.ErrorCode!="AUTH_REQUIRED"||r.OutputText!=""{t.Fatalf("unsafe auth result: %+v",r)}
}
func TestMismatchedAndDuplicateReplyHeld(t *testing.T){
 for _,mode:=range []string{"wrong-id","duplicate","malformed","exit-error","silence"}{
  t.Run(mode,func(t *testing.T){
   r:=runStub(t,mode,syntheticRequest(),5*time.Second)
   if r.Status=="COMPLETED"||r.OwnerEffectAccepted {t.Fatalf("unsafe %s outcome: %+v",mode,r)}
  })
 }
}
func TestNotifyThenResponse(t *testing.T){
 r:=runStub(t,"notification",syntheticRequest(),5*time.Second)
 if r.Status!="COMPLETED"||r.OutputText!="from same id"{t.Fatalf("lost correlated response: %+v",r)}
}
func TestTimeoutIsUnknownNoRetry(t *testing.T){
 r:=runStub(t,"timeout",syntheticRequest(),100*time.Millisecond)
 if r.Status!="UNKNOWN"||r.ErrorCode!="TIMEOUT_OR_CANCELLED"{t.Fatalf("unsafe timeout: %+v",r)}
}
func TestMissingBinaryHeld(t *testing.T){
 r:=runOnce(context.Background(),syntheticRequest(),"/no/such/suprachat-command")
 if r.Status!="UNKNOWN"||r.ErrorCode!="PROCESS_UNAVAILABLE"{t.Fatalf("unexpected result: %+v",r)}
}
func TestInvalidRequestRefusedBeforeSpawn(t *testing.T){
 req:=syntheticRequest();req.Prompt=""
 r:=runOnce(context.Background(),req,"/no/such/suprachat-command")
 if r.Status!="REFUSED"||r.ErrorCode!="INVALID_REQUEST"{t.Fatalf("must validate before child start: %+v",r)}
}
func TestParserRejectsExtraRequestFields(t *testing.T){
 var request Request
 err:=decodeOne([]byte(`{"schema":"suprachat-executor-request/v1","operation_id":"op-12345678","assignment_id":"assignment-12345","authority_revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","model":"synthetic-model","prompt":"synthetic","shell":"sh -c unsafe"}`),&request)
 if err==nil{t.Fatal("unexpected arbitrary operation field permitted")}
}
func TestPromptAndCredentialNotEmittedOnRefusal(t *testing.T) {
 req:=syntheticRequest();req.Prompt="Synthetic prompt: SECRET_FIXTURE_DO_NOT_LOG"
 r:=runStub(t,"refused",req,5*time.Second)
 b,_:=json.Marshal(r)
 if strings.Contains(string(b),"SECRET_FIXTURE_DO_NOT_LOG"){t.Fatalf("private prompt leaked into failed receipt")}
}


func TestChatGPTWebUISessionNotSilentlySubstituted(t *testing.T) {
 req:=syntheticRequest()
 req.ProviderBinding="chatgpt_webui"
 r:=runOnce(context.Background(),req,"/no/such/suprachat-command")
 if r.Status!="REFUSED"||r.ErrorCode!="INVALID_REQUEST"||r.OwnerEffectAccepted {
  t.Fatalf("unsupported ChatGPT web product binding incorrectly admitted: %+v",r)
 }
}
