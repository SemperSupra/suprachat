// Command tunnel-verify refuses any authenticated or credential-bearing claim.
package main
import (
 "bytes"
 "encoding/json"
 "errors"
 "fmt"
 "io"
 "os"
)
func verify(data []byte)error{
 if len(data)>4096{return errors.New("unbounded receipt")}
 dec:=json.NewDecoder(bytes.NewReader(data))
 tok,err:=dec.Token();if err!=nil||tok!=json.Delim('{'){return errors.New("invalid root")}
 raw:=map[string]json.RawMessage{}
 for dec.More(){
  t,e:=dec.Token();if e!=nil{return e};key,ok:=t.(string);if !ok{return errors.New("invalid key")}
  if _,exists:=raw[key];exists{return errors.New("duplicate key")}
  var v json.RawMessage;if e=dec.Decode(&v);e!=nil{return e};raw[key]=v
 }
 if t,e:=dec.Token();e!=nil||t!=json.Delim('}') {return errors.New("invalid ending")}
 if _,e:=dec.Token();e!=io.EOF{return errors.New("trailing JSON")}
 keys:=[]string{"schema","evidence_class","real_cloudflare_https","unauthorized_denied","foreign_origin_denied","synthetic_action_readback","replay_denied","wrong_run_denied","authenticated_chatgpt","user_credentials_transferred"}
 if len(raw)!=len(keys){return errors.New("unexpected keys")}
 for _,k:=range keys{if _,ok:=raw[k];!ok{return errors.New("missing key")}}
 for _,k:=range []string{"real_cloudflare_https","unauthorized_denied","foreign_origin_denied","synthetic_action_readback","replay_denied","wrong_run_denied"}{
  if string(raw[k])!="true"{return errors.New("missing positive proof")}
 }
 for _,k:=range []string{"authenticated_chatgpt","user_credentials_transferred"}{
  if string(raw[k])!="false"{return errors.New("unsupported authenticated claim")}
 }
 var h struct {Schema string `json:"schema"`;EvidenceClass string `json:"evidence_class"`}
 if err:=json.Unmarshal(data,&h);err!=nil{return err}
 if h.Schema!="suprachat-cloudflare-preflight/v1" || h.EvidenceClass!="SYNTHETIC_ONLY_NOT_AUTHENTICATED_CHATGPT" {return errors.New("unsupported evidence class")}
 return nil
}
func main(){
 if len(os.Args)!=2{os.Exit(2)}
 b,e:=os.ReadFile(os.Args[1]);if e==nil{e=verify(b)}
 if e!=nil{fmt.Fprintln(os.Stderr,"SYNTHETIC_REMOTE_RECEIPT_REFUSED");os.Exit(1)}
 fmt.Println("QUALIFIED_CLOUDFLARE_SYNTHETIC_RECEIPT")
}
