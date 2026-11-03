package oecmirror
import (
 "bytes"
 "encoding/json"
 "strings"
 "testing"
)
const good=`{"schema":"oec-mirror-input/v1","subject":"synthetic-agent","epoch":7,"declared":[{"id":"tool.discover","effect":"READ"},{"id":"synthetic.click","effect":"SYNTHETIC"},{"id":"work.submit","effect":"EXTERNAL"}],"observed":[{"id":"synthetic.click","epoch":7,"outcome":"COMPLETED","evidence":"SYNTHETIC_TEST","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"unknown":["inference.worker","private.context.assembly"]}`
func TestMirrorReproducibleAndHonest(t *testing.T) {
 a,e:=Analyze([]byte(good));if e!=nil {t.Fatal(e)}
 b,e:=Analyze([]byte(good));if e!=nil {t.Fatal(e)}
 aa,_:=EncodeReport(a);bb,_:=EncodeReport(b)
 if !bytes.Equal(aa,bb) {t.Fatal("nondeterministic report")}
 if len(a.Proven)!=1 || a.Proven[0]!="synthetic.click" || len(a.Unobserved)!=2 || a.Declared!=3 {t.Fatalf("overclaim: %+v",a)}
 if strings.Contains(string(aa),"user content") {t.Fatal("data leaked")}
}
func mutate(t *testing.T,fn func(map[string]any)){
 t.Helper()
 var m map[string]any
 if e:=json.Unmarshal([]byte(good),&m);e!=nil{t.Fatal(e)}
 fn(m)
 b,_:=json.Marshal(m)
 if _,e:=Analyze(b);e==nil {t.Fatal("should fail closed")}
}
func TestUnknownPayloadFieldRejected(t *testing.T){mutate(t,func(m map[string]any){m["raw_prompt"]="DO_NOT_EXPORT"})}
func TestEpochStalenessRejected(t *testing.T){mutate(t,func(m map[string]any){m["epoch"]=float64(8)})}
func TestExternalNotPromotedFromSynthetic(t *testing.T){mutate(t,func(m map[string]any){o:=m["observed"].([]any)[0].(map[string]any);o["id"]="work.submit"})}
func TestUnregisteredObservationRejected(t *testing.T){mutate(t,func(m map[string]any){o:=m["observed"].([]any)[0].(map[string]any);o["id"]="hidden.tool"})}
func TestDuplicateObservationRejected(t *testing.T){mutate(t,func(m map[string]any){a:=m["observed"].([]any);m["observed"]=append(a,a[0])})}
func TestNoRawCredentialField(t *testing.T){mutate(t,func(m map[string]any){m["access_token"]="TEST_TOKEN"})}
func TestOversizeRejected(t *testing.T){if _,e:=Analyze(bytes.Repeat([]byte("x"),MaxInputBytes+1));e==nil{t.Fatal("size cap")}}
func TestDuplicateCapabilityRejected(t *testing.T){mutate(t,func(m map[string]any){a:=m["declared"].([]any);m["declared"]=append(a,a[0])})}
func TestInvalidRevisionRejected(t *testing.T){mutate(t,func(m map[string]any){o:=m["observed"].([]any)[0].(map[string]any);o["revision"]="not-hash"})}
