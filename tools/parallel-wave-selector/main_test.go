package main
import(
 "bytes"
 "encoding/json"
 "strings"
 "testing"
)

func pointer(s string)*string{return &s}
func mkTask(id,state,executor,branch string,paths ...string)Task{
 t:=Task{
   ID:id,Repo:"SemperSupra/suprachat",Lane:"independent-lane",
   State:state,Executor:executor,DependsOn:[]string{},
   WritePaths:paths,AcceptanceOracle:"Independent exact-head positive and negative oracle",
   EvidenceReceipt:"Public Actions exact-source receipt",
   HumanOverride:"FENCE_AND_RECONCILE",
   Audiences:[]string{"human","accessible-human","automation","agent"},
   IssueRef:"SemperSupra/suprachat#195",
   BaseSHA:strings.Repeat("a",40),
   NegativeCase:"Reject a stale or conflicting local work packet",
 }
 if state=="READY"||state=="RUNNING" {t.SourceBranch=pointer(branch);t.OracleCommand=pointer("go test ./... -count 1")}
 if state=="HELD"||state=="BLOCKED"{t.Blocker=pointer("External verified host identity is not available")}
 return t
}
func fixture()Manifest{
 return Manifest{
   Schema:schema,WorksetID:"r49-select-example",
   Authority:Authority{"SemperSupra/suprachat","SemperSupra/suprachat-private#2",
       "SemperSupra/suprachat#195",true,true,true},
   Budget:Budget{3,2,0,0,false},
   Tasks:[]Task{
     mkTask("ready-one","READY","public-gha","agent/r49-one","tools/alpha"),
     mkTask("ready-two","READY","public-gha","agent/r49-two","tools/beta"),
     mkTask("ready-three","READY","public-gha","agent/r49-three","docs/gamma"),
     mkTask("local-only","READY","local-qualified","agent/r49-local","tools/local"),
     mkTask("held-owner","BLOCKED","local-qualified","","SupraChat/Core"),
   },
   IntegrationGates:[]string{"independent-source-and-oracle","exact-head-public-ci",
    "cross-lane-peer-review","owner-approved-merge","durable-dle-reconciliation"},
 }
}
func mustFail(t *testing.T,m Manifest,contains string){
 t.Helper()
 if e:=validate(m);e==nil || !strings.Contains(e.Error(),contains){
  t.Fatalf("expected %s, got %v",contains,e)
 }
}
func TestSelectWhenLocalOutOfGas(t *testing.T){
 m:=fixture();result,err:=selectWave(m,2,0);if err!=nil{t.Fatal(err)}
 if result.Width!=2 || result.ActualDispatch || result.AuthorizationGranted{
   t.Fatalf("invalid safe selection %#v",result)
 }
 if len(result.Deferred)!=2 || len(result.Held)!=1 {t.Fatalf("missing capacity/dependency evidence: %#v",result)}
 for _,c:=range result.Selected{
   if c.SourceBranch==""||c.ExactBaseSHA!=strings.Repeat("a",40)||
    c.OracleCommand==""||c.HumanOverride!="FENCE_AND_RECONCILE"{
     t.Fatalf("incomplete weak-actor card %#v",c)
   }
 }
 result2,err:=selectWave(m,2,1);if err!=nil{t.Fatal(err)}
 if result2.Width!=3 {t.Fatalf("local capacity not used %#v",result2)}
 result3,err:=selectWave(m,0,0);if err!=nil{t.Fatal(err)}
 if result3.Width!=0 || result3.Reason=="" {t.Fatal("no capacity must be explicit")}
}
func TestConflictingBranchesAndPathsCannotCoRun(t *testing.T){
 m:=fixture()
 m.Tasks[1].SourceBranch=pointer("agent/r49-one")
 got,err:=selectWave(m,2,0);if err!=nil{t.Fatal(err)}
 if got.Width!=2 {t.Fatal("could still select disjoint independent source")}
 for _,a:=range got.Selected{for _,b:=range got.Selected{
   if a.TaskID!=b.TaskID && a.SourceBranch==b.SourceBranch {t.Fatal("same branch selected twice")}
 }}
 m=fixture()
 m.Tasks[1].WritePaths=[]string{"tools/alpha/sub"}
 got,err=selectWave(m,2,0);if err!=nil{t.Fatal(err)}
 if got.Width!=2 {t.Fatal("expected other independent lane")}
 for i:=0;i<len(got.Selected);i++{for j:=i+1;j<len(got.Selected);j++{
   for _,p:=range got.Selected[i].AllowedPaths{for _,q:=range got.Selected[j].AllowedPaths{
    if overlap(p,q){t.Fatal("overlapping paths selected")}
   }}
 }}
}
func TestRunningWorkReservesCapacityAndFencesOverlaps(t *testing.T){
 m:=fixture()
 // A verified RUNNING lane consumes one of the bounded concurrency slots,
 // even if the receiving controller reports that local worker slots are zero.
 m.Tasks[3].State="RUNNING"
 result,err:=selectWave(m,2,0)
 if err!=nil{t.Fatal(err)}
 if result.Width!=2{t.Fatalf("running lane ignored in capacity calculation: %#v",result)}
 // Only one new lane may be admitted when two are already running.
 m.Tasks[0].State="RUNNING"
 one,err:=selectWave(m,2,0)
 if err!=nil{t.Fatal(err)}
 if one.Width!=1{t.Fatalf("overcommitted already-running lanes: %#v",one)}
 // If the active local worker owns tools/alpha, overlapping READY source
 // must be deferred, even if public free slots exist for another lane.
 m=fixture()
 m.Tasks[3].State="RUNNING"
 m.Tasks[3].WritePaths=[]string{"tools/alpha/sub"}
 overlapping,err:=selectWave(m,2,0)
 if err!=nil{t.Fatal(err)}
 for _,c:=range overlapping.Selected{
  if c.TaskID=="ready-one"{t.Fatal("dispatched task overlapping live write ownership")}
 }
}
func TestFailClosedAuthorityAndDependencies(t *testing.T){
 cases:=[]struct{
  name string
  mutate func(*Manifest)
  want string
 }{
  {"private-minutes",func(m *Manifest){m.Budget.PrivateGHAMinutes=1},"cost-or-authority"},
  {"money",func(m *Manifest){m.Budget.PaidUSD=1},"cost-or-authority"},
  {"recursive",func(m *Manifest){m.Budget.RecursiveDelegation=true},"cost-or-authority"},
  {"human-priority",func(m *Manifest){m.Authority.HumanCanPreempt=false},"owner-authority"},
  {"owner-merge",func(m *Manifest){m.Authority.OwnerControlsMerge=false},"owner-authority"},
  {"audience",func(m *Manifest){m.Tasks[0].Audiences=[]string{"agent","automation","human"}},"human-priority-or-audience"},
  {"unfenced",func(m *Manifest){m.Tasks[0].HumanOverride="IGNORE"},"human-priority-or-audience"},
  {"unsafe-path",func(m *Manifest){m.Tasks[0].WritePaths=[]string{"../escape"}},"unsafe-path"},
  {"missing-oracle",func(m *Manifest){m.Tasks[0].OracleCommand=nil},"unsafe-ready-card"},
  {"missing-sha",func(m *Manifest){m.Tasks[0].BaseSHA="main"},"task-card-unpinned"},
  {"unapproved-repo",func(m *Manifest){m.Tasks[0].Repo="AlienOrg/unreviewed"},"unapproved-repository"},
  {"malformed-issue",func(m *Manifest){m.Tasks[0].IssueRef="SemperSupra/unbound"},"task-card-unpinned"},
  {"shell-injection-oracle",func(m *Manifest){m.Tasks[0].OracleCommand=pointer("go test ./...; rm -rf /")},"unsafe-ready-card"},
  {"malformed-branch",func(m *Manifest){m.Tasks[0].SourceBranch=pointer("agent/r49:invalid")},"unsafe-ready-card"},
  {"bad-path-metachar",func(m *Manifest){m.Tasks[0].WritePaths=[]string{"tools/alpha;delete"}},"unsafe-path"},
  {"forbidden-executor",func(m *Manifest){m.Tasks[0].Repo="SemperSupra/suprachat-private"},"private-in-public-runner"},
  {"pending-dep",func(m *Manifest){m.Tasks[0].DependsOn=[]string{"held-owner"}},"unfinished-dependency"},
  {"cycle",func(m *Manifest){m.Tasks[4].DependsOn=[]string{"ready-one"};m.Tasks[0].DependsOn=[]string{"held-owner"}},"unfinished-dependency"},
  {"dup-id",func(m *Manifest){m.Tasks[1].ID="ready-one"},"duplicate-task"},
  {"wrong-gates",func(m *Manifest){m.IntegrationGates[3]="auto-merge"},"integration-order-invalid"},
 }
 for _,tc:=range cases{
  t.Run(tc.name,func(t *testing.T){m:=fixture();tc.mutate(&m);mustFail(t,m,tc.want)})
 }
}
func TestStrictReadOnlyJSON(t *testing.T){
 m:=fixture()
 b,err:=json.Marshal(m);if err!=nil{t.Fatal(err)}
 decoded,err:=readManifest(bytes.NewReader(b));if err!=nil{t.Fatal(err)}
 if len(decoded.Tasks)!=5{t.Fatal("lost tasks")}
 if _,err=readManifest(strings.NewReader(string(b)+`{"junk":true}`));err==nil{t.Fatal("accepted trailing JSON")}
 corrupt:=strings.Replace(string(b),`"paid_usd":0`,`"paid_usd":0,"unreviewed_authority":true`,1)
 if _,err=readManifest(strings.NewReader(corrupt));err==nil{t.Fatal("accepted arbitrary authority")}
 if _,err=readManifest(strings.NewReader(strings.Repeat("a",1048577)));err==nil{t.Fatal("unbounded manifest")}
}
func TestDeterministicAcrossPermutation(t *testing.T){
 m:=fixture();one,_:=selectWave(m,2,0)
 for i,j:=0,len(m.Tasks)-1;i<j;i,j=i+1,j-1 {m.Tasks[i],m.Tasks[j]=m.Tasks[j],m.Tasks[i]}
 two,_:=selectWave(m,2,0)
 for i:=range one.Selected{
  if one.Selected[i].TaskID!=two.Selected[i].TaskID{
   t.Fatal("selection changes with arbitrary input order")
  }
 }
}
