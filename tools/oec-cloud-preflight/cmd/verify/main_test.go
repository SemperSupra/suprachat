package main
import (
 "encoding/json"
 "strings"
 "testing"
)
func validReceipt() string {
 v:=Receipt{Schema:"suprachat-cloud-preflight-receipt/v1",
 EvidenceClass:"SYNTHETIC_ONLY_NOT_AUTHENTICATED_CHATGPT",Platform:"Linux",
 SyntheticTLS:true,BeforeAfterReadback:true,OneUseReplayRejected:true,TeardownRevoked:true}
 b,_:=json.Marshal(v);return string(b)
}
func TestStrictVerifierNegatives(t *testing.T){
 t.Setenv("RUNNER_OS","")
 good:=validReceipt()
 if err:=validate([]byte(good));err!=nil{t.Fatalf("valid receipt rejected: %v",err)}
 cases:=map[string]string{
  "duplicate_key":strings.Replace(good,`"synthetic_tls":true`,`"synthetic_tls":false,"synthetic_tls":true`,1),
  "escaped_duplicate_key":strings.Replace(good,`"synthetic_tls":true`,`"synthetic_tls":true,"synthetic_\u0074ls":true`,1),
  "extra_field":strings.Replace(good,`"live_local_hil":false`,`"live_local_hil":false,"raw_cookie":"secret"`,1),
  "missing_field":strings.Replace(good,`,"live_local_hil":false`,"",1),
  "unsupported_claim":strings.Replace(good,`"authenticated_chatgpt":false`,`"authenticated_chatgpt":true`,1),
  "false_teardown":strings.Replace(good,`"teardown_revoked":true`,`"teardown_revoked":false`,1),
  "incorrect_class":strings.Replace(good,"SYNTHETIC_ONLY_NOT_AUTHENTICATED_CHATGPT","REAL_AUTHENTICATED",1),
  "trailing_object":good+`{"schema":"fake"}`,
  "array_root":"["+good+"]",
  "oversized":good+strings.Repeat(" ",4097),
  "non_boolean":strings.Replace(good,`"synthetic_tls":true`,`"synthetic_tls":1`,1),
 }
 for name,blob:=range cases{t.Run(name,func(t *testing.T){
   if err:=validate([]byte(blob));err==nil{t.Fatal("unsafe receipt accepted")}
 })}
 t.Run("platform_mismatch",func(t *testing.T){
  t.Setenv("RUNNER_OS","Windows")
  if err:=validate([]byte(good));err==nil{t.Fatal("wrong runner platform accepted")}
 })
}
