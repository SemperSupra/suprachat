package oecmirror

import (
  "bytes"
  "crypto/sha256"
  "encoding/hex"
  "encoding/json"
  "errors"
  "fmt"
  "io"
  "regexp"
  "sort"
)

const MaxInputBytes = 262144
var ident = regexp.MustCompile(`^[a-z][a-z0-9_.-]{0,63}$`)
var digest = regexp.MustCompile(`^[a-f0-9]{64}$`)

type Input struct {
  Schema string `json:"schema"`
  Subject string `json:"subject"`
  Epoch uint64 `json:"epoch"`
  Declared []Capability `json:"declared"`
  Observed []Observation `json:"observed"`
  Unknown []string `json:"unknown"`
}
type Capability struct {
  ID string `json:"id"`
  Effect string `json:"effect"`
}
type Observation struct {
  ID string `json:"id"`
  Epoch uint64 `json:"epoch"`
  Outcome string `json:"outcome"`
  Evidence string `json:"evidence"`
  Revision string `json:"revision"`
}
type Report struct {
  Schema string `json:"schema"`
  Subject string `json:"subject"`
  InputSHA256 string `json:"input_sha256"`
  Declared int `json:"declared_count"`
  Observed int `json:"observed_count"`
  Unobserved []string `json:"unobserved"`
  Proven []string `json:"synthetically_qualified"`
  Unknown []string `json:"unknown"`
}
func validID(s string) bool { return ident.MatchString(s) }
func Analyze(data []byte) (Report,error) {
  if len(data)>MaxInputBytes {return Report{}, errors.New("manifest too large")}
  dec:=json.NewDecoder(bytes.NewReader(data))
  dec.DisallowUnknownFields()
  var in Input
  if err:=dec.Decode(&in);err!=nil{return Report{}, fmt.Errorf("invalid manifest: %w",err)}
  var extra any
  if err:=dec.Decode(&extra);err!=io.EOF {return Report{}, errors.New("trailing JSON or extra data")}
  if in.Schema!="oec-mirror-input/v1" || !validID(in.Subject) || in.Epoch==0 {return Report{}, errors.New("invalid schema, subject or epoch")}
  if len(in.Declared)>512 || len(in.Observed)>512 || len(in.Unknown)>512 {return Report{},errors.New("unbounded manifest")}
  declared:=make(map[string]string)
  for _,c:=range in.Declared {
    if !validID(c.ID) {return Report{},errors.New("invalid capability ID")}
    if c.Effect!="READ" && c.Effect!="SYNTHETIC" && c.Effect!="EXTERNAL" {return Report{},errors.New("invalid effect")}
    if _,ok:=declared[c.ID];ok {return Report{},errors.New("duplicate capability")}
    declared[c.ID]=c.Effect
  }
  seen:=make(map[string]bool)
  qualified:=make([]string,0)
  for _,o:=range in.Observed {
    effect,ok:=declared[o.ID]
    if !ok {return Report{},fmt.Errorf("unregistered observed action: %q",o.ID)}
    if seen[o.ID] {return Report{},errors.New("duplicate observation")}
    seen[o.ID]=true
    if o.Epoch!=in.Epoch {return Report{},errors.New("stale session epoch")}
    if o.Outcome!="COMPLETED" && o.Outcome!="FAILED" && o.Outcome!="HELD" && o.Outcome!="UNKNOWN" {return Report{},errors.New("invalid outcome")}
    if o.Evidence!="SYNTHETIC_TEST" && o.Evidence!="DIRECT_RUNNER" && o.Evidence!="PROVIDER_REPORTED" && o.Evidence!="INFERRED" && o.Evidence!="UNKNOWN" {return Report{},errors.New("invalid evidence class")}
    if !digest.MatchString(o.Revision) {return Report{},errors.New("revision must be sha256")}
    if effect=="EXTERNAL" && o.Outcome=="COMPLETED" && o.Evidence=="SYNTHETIC_TEST" {return Report{},errors.New("synthetic result cannot qualify external effect")}
    if o.Outcome=="COMPLETED" && o.Evidence=="SYNTHETIC_TEST" && effect=="SYNTHETIC" {qualified=append(qualified,o.ID)}
  }
  unknownSet:=make(map[string]bool)
  for _,u:=range in.Unknown {
    if !validID(u) || unknownSet[u] {return Report{},errors.New("invalid or duplicated unknown boundary")}
    unknownSet[u]=true
  }
  unobserved:=make([]string,0)
  for id:=range declared {if !seen[id] {unobserved=append(unobserved,id)}}
  sort.Strings(unobserved);sort.Strings(qualified);sort.Strings(in.Unknown)
  sum:=sha256.Sum256(data)
  return Report{Schema:"oec-mirror-report/v1",Subject:in.Subject,InputSHA256:hex.EncodeToString(sum[:]),Declared:len(in.Declared),Observed:len(in.Observed),Unobserved:unobserved,Proven:qualified,Unknown:in.Unknown},nil
}
func EncodeReport(report Report) ([]byte,error) {return json.MarshalIndent(report,"","  ")}
