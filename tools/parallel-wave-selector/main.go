// R49: deterministic read-only antichain selector for bounded DLE actor cards.
// A selector is NOT an executor, workflow scheduler, GitHub writer, or authority issuer.
// Requires the matching R48 Python critic as an independent validation gate.
package main

import (
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "regexp"
 "sort"
 "strings"
)

const schema = "suprachat-parallel-workset/v1"
var ident=regexp.MustCompile(`^[a-z][a-z0-9-]{2,45}$`)
var sha=regexp.MustCompile(`^[0-9a-f]{40}// R49: deterministic read-only antichain selector for bounded DLE actor cards.
// A selector is NOT an executor, workflow scheduler, GitHub writer, or authority issuer.
// Requires the matching R48 Python critic as an independent validation gate.
package main

import (
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "regexp"
 "sort"
 "strings"
)

const schema = "suprachat-parallel-workset/v1"
var ident=regexp.MustCompile(`^[a-z][a-z0-9-]{2,45}$`)
)
var sourceBranch=regexp.MustCompile(`^(agent|impl)/[A-Za-z0-9._/-]{3,128}// R49: deterministic read-only antichain selector for bounded DLE actor cards.
// A selector is NOT an executor, workflow scheduler, GitHub writer, or authority issuer.
// Requires the matching R48 Python critic as an independent validation gate.
package main

import (
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "regexp"
 "sort"
 "strings"
)

const schema = "suprachat-parallel-workset/v1"
var ident=regexp.MustCompile(`^[a-z][a-z0-9-]{2,45}$`)
)
var safeFile=regexp.MustCompile(`^[A-Za-z0-9._/-]{1,160}// R49: deterministic read-only antichain selector for bounded DLE actor cards.
// A selector is NOT an executor, workflow scheduler, GitHub writer, or authority issuer.
// Requires the matching R48 Python critic as an independent validation gate.
package main

import (
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "regexp"
 "sort"
 "strings"
)

const schema = "suprachat-parallel-workset/v1"
var ident=regexp.MustCompile(`^[a-z][a-z0-9-]{2,45}$`)
)
var issueRef=regexp.MustCompile(`^SemperSupra/[A-Za-z0-9._-]+#[1-9][0-9]*// R49: deterministic read-only antichain selector for bounded DLE actor cards.
// A selector is NOT an executor, workflow scheduler, GitHub writer, or authority issuer.
// Requires the matching R48 Python critic as an independent validation gate.
package main

import (
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "regexp"
 "sort"
 "strings"
)

const schema = "suprachat-parallel-workset/v1"
var ident=regexp.MustCompile(`^[a-z][a-z0-9-]{2,45}$`)
)
var oracle=regexp.MustCompile(`^[A-Za-z0-9._ /:-]{12,220}// R49: deterministic read-only antichain selector for bounded DLE actor cards.
// A selector is NOT an executor, workflow scheduler, GitHub writer, or authority issuer.
// Requires the matching R48 Python critic as an independent validation gate.
package main

import (
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "regexp"
 "sort"
 "strings"
)

const schema = "suprachat-parallel-workset/v1"
var ident=regexp.MustCompile(`^[a-z][a-z0-9-]{2,45}$`)
)

type Authority struct {
 PublicRepo string `json:"public_repo"`
 PrivateDLE string `json:"private_dle"`
 OwnerIssue string `json:"owner_issue"`
 HumanCanPreempt bool `json:"human_can_preempt"`
 SingleSessionAuthority bool `json:"single_session_authority"`
 OwnerControlsMerge bool `json:"owner_controls_merge"`
}
type Budget struct {
 MaxConcurrent int `json:"max_concurrent"`
 MaxPublicGHAJobs int `json:"max_public_gha_jobs"`
 PrivateGHAMinutes int `json:"private_gha_minutes"`
 PaidUSD int `json:"paid_usd"`
 RecursiveDelegation bool `json:"recursive_delegation"`
}
type Task struct {
 ID string `json:"id"`
 Repo string `json:"repo"`
 Lane string `json:"lane"`
 State string `json:"state"`
 Executor string `json:"executor"`
 DependsOn []string `json:"depends_on"`
 WritePaths []string `json:"write_paths"`
 AcceptanceOracle string `json:"acceptance_oracle"`
 EvidenceReceipt string `json:"evidence_receipt"`
 HumanOverride string `json:"human_override_response"`
 Audiences []string `json:"audiences"`
 Blocker *string `json:"blocker"`
 IssueRef string `json:"issue_ref"`
 SourceBranch *string `json:"source_branch"`
 BaseSHA string `json:"base_sha"`
 OracleCommand *string `json:"oracle_command"`
 NegativeCase string `json:"negative_case"`
}
type Manifest struct {
 Schema string `json:"schema"`
 WorksetID string `json:"workset_id"`
 Authority Authority `json:"authority"`
 Budget Budget `json:"budget"`
 Tasks []Task `json:"tasks"`
 IntegrationGates []string `json:"integration_gates"`
}
type Card struct {
 TaskID string `json:"task_id"`
 IssueRef string `json:"issue_ref"`
 Repository string `json:"repo"`
 SourceBranch string `json:"branch"`
 ExactBaseSHA string `json:"base_sha"`
 AllowedPaths []string `json:"allowed_paths"`
 OracleCommand string `json:"oracle_command"`
 NegativeCase string `json:"negative_case"`
 EvidenceReceipt string `json:"expected_receipt"`
 HumanOverride string `json:"human_override_response"`
}
type Result struct {
 Schema string `json:"schema"`
 WorksetID string `json:"workset_id"`
 Selected []Card `json:"selected"`
 Ready []string `json:"ready"`
 Deferred []string `json:"deferred"`
 Held []string `json:"held"`
 Width int `json:"width"`
 CapacityPublic int `json:"capacity_public"`
 CapacityLocal int `json:"capacity_local"`
 Reason string `json:"reason"`
 ActualDispatch bool `json:"actual_dispatch"`
 AuthorizationGranted bool `json:"authorization_granted"`
}

func readManifest(r io.Reader)(Manifest,error){
 var m Manifest
 body,err:=io.ReadAll(io.LimitReader(r,1048577))
 if err!=nil || len(body)>1048576{return m,errors.New("manifest-size-invalid")}
 d:=json.NewDecoder(strings.NewReader(string(body)))
 d.DisallowUnknownFields()
 if err=d.Decode(&m);err!=nil{return m,fmt.Errorf("manifest-schema-invalid: %w",err)}
 var extra any
 if err=d.Decode(&extra);err!=io.EOF{return m,errors.New("trailing-data")}
 return m,validate(m)
}
func pathSafe(p string)bool{
 if !safeFile.MatchString(p)||strings.HasPrefix(p,"/")||strings.HasSuffix(p,"/")||
    strings.IndexByte(p,92)>=0||strings.IndexByte(p,0)>=0||
    strings.IndexByte(p,13)>=0||strings.IndexByte(p,10)>=0{return false}
 for _,part:=range strings.Split(p,"/"){if part==""||part=="."||part==".."{return false}}
 return true
}
func overlap(a,b string)bool{return a==b||strings.HasPrefix(a,b+"/")||strings.HasPrefix(b,a+"/")}
func clash(a,b Task)bool{
 if a.Repo!=b.Repo{return false}
 if a.SourceBranch!=nil&&b.SourceBranch!=nil&&*a.SourceBranch==*b.SourceBranch{return true}
 for _,x:=range a.WritePaths{for _,y:=range b.WritePaths{if overlap(x,y){return true}}}
 return false
}
func validate(m Manifest)error{
 if m.Schema!=schema||!ident.MatchString(m.WorksetID){return errors.New("wrong-manifest-version")}
 a:=m.Authority
 if a.PublicRepo!="SemperSupra/suprachat"||a.PrivateDLE!="SemperSupra/suprachat-private#2"||
 a.OwnerIssue!="SemperSupra/suprachat#195"||!a.HumanCanPreempt||!a.SingleSessionAuthority||!a.OwnerControlsMerge {
  return errors.New("owner-authority-invalid")
 }
 b:=m.Budget
 if b.MaxConcurrent<2||b.MaxConcurrent>4||b.MaxPublicGHAJobs<1||b.MaxPublicGHAJobs>2||
 b.PrivateGHAMinutes!=0||b.PaidUSD!=0||b.RecursiveDelegation{return errors.New("cost-or-authority-boundary")}
 if len(m.Tasks)<2||len(m.Tasks)>24{return errors.New("task-count-invalid")}
 gates:=[]string{"independent-source-and-oracle","exact-head-public-ci","cross-lane-peer-review","owner-approved-merge","durable-dle-reconciliation"}
 if len(m.IntegrationGates)!=len(gates){return errors.New("gate-count-invalid")}
 for i,x:=range gates{if m.IntegrationGates[i]!=x{return errors.New("integration-order-invalid")}}
 refs:=map[string]Task{}
 for _,t:=range m.Tasks{
  if !ident.MatchString(t.ID)||!ident.MatchString(t.Lane){return errors.New("task-identifier-invalid")}
  if _,exists:=refs[t.ID];exists{return errors.New("duplicate-task")}
  refs[t.ID]=t
  if t.HumanOverride!="FENCE_AND_RECONCILE"||len(t.Audiences)!=4{return errors.New("human-priority-or-audience")}
  aud:=map[string]bool{};for _,x:=range t.Audiences{aud[x]=true}
  for _,x:=range []string{"human","accessible-human","automation","agent"}{if !aud[x]{return errors.New("audience-parity")}}
  if t.Executor!="public-gha"&&t.Executor!="local-qualified"&&t.Executor!="private-governed" {return errors.New("executor-invalid")}
  switch t.Repo {
   case "SemperSupra/suprachat","SemperSupra/suprachat-private",
    "SemperSupra/android-publishing-foundry","SemperSupra/model-spelunker":
   default:return errors.New("unapproved-repository")
  }
  if strings.HasSuffix(t.Repo,"-private")&&t.Executor=="public-gha"{return errors.New("private-in-public-runner")}
  if !sha.MatchString(t.BaseSHA)||!issueRef.MatchString(t.IssueRef)||len(t.NegativeCase)<12||len(t.EvidenceReceipt)<5||len(t.AcceptanceOracle)<12{return errors.New("task-card-unpinned")}
  if len(t.WritePaths)==0{return errors.New("unowned-task")}
  for _,x:=range t.WritePaths{if !pathSafe(x){return errors.New("unsafe-path")}}
  if t.State!="READY"&&t.State!="RUNNING"&&t.State!="DONE"&&t.State!="BLOCKED"&&t.State!="HELD"{return errors.New("state-invalid")}
  if t.State=="READY"||t.State=="RUNNING"{
    if t.SourceBranch==nil||!sourceBranch.MatchString(*t.SourceBranch)||!pathSafe(*t.SourceBranch)||
       t.OracleCommand==nil||!oracle.MatchString(*t.OracleCommand)||t.Blocker!=nil{return errors.New("unsafe-ready-card")}
    if t.Executor=="private-governed"{return errors.New("private-worker-not-admitted")}
  }else if (t.State=="BLOCKED"||t.State=="HELD")&&(t.Blocker==nil||len(*t.Blocker)<10){return errors.New("missing-held-reason")}
 }
 for _,t:=range m.Tasks{
  for _,dep:=range t.DependsOn{
   d,ok:=refs[dep]
   if !ok||dep==t.ID{return errors.New("invalid-dependency")}
   if (t.State=="READY"||t.State=="RUNNING")&&d.State!="DONE"{return errors.New("unfinished-dependency")}
  }
 }
 seen:=map[string]bool{};in:=map[string]bool{}
 var visit func(string)error
 visit=func(id string)error {
  if in[id]{return errors.New("cyclic-dependency")}
  if seen[id]{return nil}
  in[id]=true;for _,x:=range refs[id].DependsOn{if err:=visit(x);err!=nil{return err}}
  in[id]=false;seen[id]=true;return nil
 }
 for id:=range refs{if err:=visit(id);err!=nil{return err}}
 return nil
}
func selectWave(m Manifest,publicSlots,localSlots int)(Result,error){
 if err:=validate(m);err!=nil{return Result{},err}
 if publicSlots<0||publicSlots>m.Budget.MaxPublicGHAJobs||localSlots<0||localSlots>m.Budget.MaxConcurrent{return Result{},errors.New("unadmitted-worker-capacity")}
 ready,running,held:=[]Task{},[]Task{},[]string{}
 for _,t:=range m.Tasks{
  if t.State=="READY"{ready=append(ready,t)}
  if t.State=="RUNNING"{running=append(running,t)}
  if t.State=="HELD"||t.State=="BLOCKED"{held=append(held,t.ID)}
 }
 if len(running)>m.Budget.MaxConcurrent{return Result{},errors.New("running-capacity-exceeded")}
 for i,a:=range running{
   for _,b:=range running[i+1:]{
     if clash(a,b){return Result{},errors.New("existing-running-lanes-conflict")}
   }
 }
 maxNew:=m.Budget.MaxConcurrent-len(running)
 sort.Slice(ready,func(i,j int)bool{return ready[i].ID<ready[j].ID})
 sort.Strings(held)
 best:=[]Task{}
 var walk func(int,[]Task,int,int)
 walk=func(i int,sel []Task,pub,loc int){
  if len(sel)>len(best){best=append([]Task{},sel...)}
  if i==len(ready)||len(sel)==maxNew{return}
  if len(sel)+(len(ready)-i)<=len(best){return}
  t:=ready[i]
  resourceOK:=false
  if t.Executor=="public-gha"{resourceOK=pub<publicSlots}
  if t.Executor=="local-qualified"{resourceOK=loc<localSlots}
  noClash:=true
  for _,x:=range sel{if clash(x,t){noClash=false;break}}
  for _,x:=range running{if clash(x,t){noClash=false;break}}
  if resourceOK&&noClash{
    next:=append(append([]Task{},sel...),t)
    np,nl:=pub,loc
    if t.Executor=="public-gha"{np++}else{nl++}
    walk(i+1,next,np,nl)
  }
  walk(i+1,sel,pub,loc)
 }
 walk(0,nil,0,0)
 chosen:=map[string]bool{};cards:=[]Card{};allReady:=[]string{};deferred:=[]string{}
 for _,t:=range best{
  chosen[t.ID]=true
  cards=append(cards,Card{t.ID,t.IssueRef,t.Repo,*t.SourceBranch,t.BaseSHA,t.WritePaths,*t.OracleCommand,t.NegativeCase,t.EvidenceReceipt,t.HumanOverride})
 }
 for _,t:=range ready{
  allReady=append(allReady,t.ID)
  if !chosen[t.ID]{deferred=append(deferred,t.ID)}
 }
 reason:="maximal-disjoint-wave-within-admitted-capacity"
 if len(best)==0{reason="no-admitted-executor-capacity-or-path-disjointness"}
 if maxNew==0{reason="all-concurrent-capacity-occupied-by-running-work"}
 if len(best)==1{reason="only-one-independent-capacity-admitted-task"}
 return Result{"suprachat-parallel-wave-selection/v1",m.WorksetID,cards,allReady,deferred,held,len(cards),publicSlots,localSlots,reason,false,false},nil
}
func main(){
 path:=flag.String("manifest","","R48 manifest path (trusted local workspace only)")
 public:=flag.Int("public-slots",2,"available public GHA jobs, max 2")
 local:=flag.Int("local-slots",0,"admitted local worker slots, 0 when exhausted")
 flag.Parse()
 if *path=="" {fmt.Fprintln(os.Stderr,"manifest required");os.Exit(2)}
 f,err:=os.Open(*path);if err!=nil{fmt.Fprintln(os.Stderr,"manifest unavailable");os.Exit(2)}
 defer f.Close()
 m,err:=readManifest(f);if err!=nil{fmt.Fprintln(os.Stderr,err);os.Exit(2)}
 result,err:=selectWave(m,*public,*local);if err!=nil{fmt.Fprintln(os.Stderr,err);os.Exit(2)}
 if err=json.NewEncoder(os.Stdout).Encode(result);err!=nil{os.Exit(2)}
}
