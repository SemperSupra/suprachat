// Command verify independently refuses unsupported or ambiguous synthetic receipts.
package main
import (
 "bytes"
 "encoding/json"
 "errors"
 "fmt"
 "io"
 "os"
)
type Receipt struct {
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
func validate(blob []byte) error {
 if len(blob)>4096 {return errors.New("oversized receipt")}
 dec:=json.NewDecoder(bytes.NewReader(blob))
 start,err:=dec.Token();if err!=nil||start!=json.Delim('{'){return errors.New("not an object")}
 raw:=make(map[string]json.RawMessage)
 for dec.More(){
  token,err:=dec.Token();if err!=nil{return errors.New("bad key")}
  key,ok:=token.(string);if !ok{return errors.New("invalid key")}
  if _,exists:=raw[key];exists{return errors.New("duplicate key")}
  var value json.RawMessage
  if err=dec.Decode(&value);err!=nil{return errors.New("invalid field value")}
  raw[key]=value
 }
 end,err:=dec.Token();if err!=nil||end!=json.Delim('}') {return errors.New("invalid object ending")}
 if _,err=dec.Token();err!=io.EOF{return errors.New("trailing JSON")}
 if len(raw)!=10{return errors.New("unexpected field set")}
 for _,key:=range []string{"schema","evidence_class","platform","synthetic_tls","before_after_readback","one_use_replay_rejected","teardown_revoked","sensitive_content_exported","authenticated_chatgpt","live_local_hil"} {
  if _,ok:=raw[key];!ok{return errors.New("required field missing")}
 }
 var v Receipt
 if err:=json.Unmarshal(blob,&v);err!=nil{return errors.New("invalid typed receipt")}
 if v.Schema!="suprachat-cloud-preflight-receipt/v1" ||
    v.EvidenceClass!="SYNTHETIC_ONLY_NOT_AUTHENTICATED_CHATGPT" ||
    !v.SyntheticTLS || !v.BeforeAfterReadback || !v.OneUseReplayRejected || !v.TeardownRevoked ||
    v.SensitiveContentExported || v.AuthenticatedChatGPT || v.LiveLocalHIL {
  return errors.New("unsupported receipt claim")
 }
 expected:=os.Getenv("RUNNER_OS")
 if expected!="" && v.Platform!=expected{return errors.New("runner platform mismatch")}
 return nil
}
func main(){
 if len(os.Args)!=2{fmt.Fprintln(os.Stderr,"usage: verify <synthetic receipt>");os.Exit(2)}
 data,err:=os.ReadFile(os.Args[1])
 if err==nil{err=validate(data)}
 if err!=nil{fmt.Fprintln(os.Stderr,"CLOUD_PREFLIGHT_RECEIPT_REJECTED:",err);os.Exit(1)}
 fmt.Println("QUALIFIED_CLOUD_PREFLIGHT_SYNTHETIC_ONLY")
}
