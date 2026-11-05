package provision
import (
 "encoding/json"
 "reflect"
 "strings"
 "testing"
)
func modelA(id,scope,suffix,mode string)Request{
 r:=Request{InstallID:id,Mode:mode,Cap:Capabilities{TunnelManage:true,RuntimeKey:true,CodexCLI:true}}
 r.Desired=[]Resource{
  {Kind:Tunnel,Key:"tunnel-"+suffix,Version:"v1",Scope:scope},
  {Kind:Runtime,Key:"runtime-"+suffix,Version:"v1",Scope:scope},
  {Kind:ChatGPT,Key:"connection-"+suffix,Version:"v1",Scope:scope},
 }
 return r
}
func TestModelATwoInstallationsSameWorkspaceIndependent(t *testing.T){
 home:=modelA("install-home","workspace-user","home","install")
 work:=modelA("install-work","workspace-user","work","install")
 p,e:=BuildFleet([]Request{work,home});if e!=nil{t.Fatal(e)}
 if p.Architecture!="model_a_independent_private_tunnels"||!p.DryRun||p.ChangesApplied||p.ChatGPTConnected{t.Fatal("unsupported fleet acceptance")}
 if len(p.Installations)!=2 || p.Installations[0].InstallID!="install-home"||p.Installations[1].InstallID!="install-work"{t.Fatal("unstable per-install ordering")}
 for _,ip:=range p.Installations{
  if operation(ip,Tunnel)!="create_owned_tunnel"||operation(ip,Runtime)!="connect_owned_runtime"||
    operation(ip,ChatGPT)!="operator_register_chatgpt_plugin"{t.Fatal("missing private isolated setup step")}
  for _,s:=range ip.Steps{if s.Scope!="workspace-user"{t.Fatal("lost resource scope in plan")}}
 }
 raw,e:=json.Marshal(p);if e!=nil{t.Fatal(e)}
 if strings.Contains(string(raw),"chatgpt_connected\":true"){t.Fatal("invented ChatGPT state")}
}
func TestModelACrossWorkspaceSameKeyMustNotAlias(t *testing.T){
 a:=modelA("install-aaa","workspace-one","same","install")
 b:=modelA("install-bbb","workspace-two","same","install")
 plan,e:=BuildFleet([]Request{a,b});if e!=nil||len(plan.Installations)!=2{t.Fatal("identical key under separate workspace scope rejected")}
 r:=a;r.Desired=append(r.Desired,Resource{Kind:Tunnel,Key:"tunnel-same",Version:"v1",Scope:"workspace-two"})
 if _,e=Build(r);e!=nil{t.Fatal("scope-distinct resource key incorrectly considered duplicate")}
}
func TestModelARejectSameWorkspaceConnectionKey(t *testing.T){
 a:=modelA("install-aaa","workspace-one","same","install")
 b:=modelA("install-bbb","workspace-one","same","install")
 if _,e:=BuildFleet([]Request{a,b});e==nil{t.Fatal("same scoped tunnel key across installers accepted")}
}
func TestModelARejectSharedRemoteTunnel(t *testing.T){
 a:=modelA("install-aaa","workspace-one","aaa","uninstall")
 b:=modelA("install-bbb","workspace-one","bbb","uninstall")
 for _,req:=range []*Request{&a,&b}{
  req.Observed=[]Resource{
   {Kind:Tunnel,Key:"tunnel-"+strings.TrimPrefix(req.InstallID,"install-"),
    Version:"v1",Scope:"workspace-one",Owner:req.InstallID,RemoteID:"tunnel_duplicate"},
  }
 }
 if _,e:=BuildFleet([]Request{a,b});e==nil{t.Fatal("same remote tunnel used by two separate runtimes")}
}
func TestModelAMixedTeardownNeverTouchesOtherInstall(t *testing.T){
 home:=modelA("install-home","workspace-user","home","uninstall")
 home.Observed=[]Resource{
  {Kind:Tunnel,Key:"tunnel-home",Version:"v1",Scope:"workspace-user",Owner:home.InstallID,RemoteID:"tunnel_home"},
  {Kind:Runtime,Key:"runtime-home",Version:"v1",Scope:"workspace-user",Owner:home.InstallID},
  {Kind:ChatGPT,Key:"connection-home",Version:"v1",Scope:"workspace-user",Owner:home.InstallID,DetachedVerified:true},
 }
 home.Observed[0].RecordedFingerprint=digest(home.Observed[0])
 home.Observed[0].ObservedFingerprint=home.Observed[0].RecordedFingerprint
 work:=modelA("install-work","workspace-user","work","install")
 p,e:=BuildFleet([]Request{home,work});if e!=nil{t.Fatal(e)}
 if operation(p.Installations[0],Tunnel)!="delete_owned_tunnel"{t.Fatal("verified home-owned tunnel not independently removable")}
 if operation(p.Installations[1],Tunnel)!="create_owned_tunnel"{t.Fatal("work setup corrupted by home teardown")}
 home.Observed[2].DetachedVerified=false
 p,e=BuildFleet([]Request{work,home});if e!=nil{t.Fatal(e)}
 if operation(p.Installations[0],Tunnel)!="defer_tunnel_delete"{t.Fatal("incomplete plugin disconnection deleted home tunnel")}
 if operation(p.Installations[1],Tunnel)!="create_owned_tunnel"{t.Fatal("other installer blocked by unrelated detachment")}
}
func TestModelARejectDuplicateInstallIDAndBounds(t *testing.T){
 r:=modelA("install-aaa","workspace-user","aaa","install")
 if _,e:=BuildFleet([]Request{r,r});e==nil{t.Fatal("duplicate install identity admitted")}
 if _,e:=BuildFleet(nil);e==nil{t.Fatal("empty fleet admitted")}
 many:=make([]Request,33)
 for i:=range many{many[i]=r;many[i].InstallID="install-a"+strings.Repeat("x",i+1)}
 if _,e:=BuildFleet(many);e==nil{t.Fatal("unbounded fleet accepted")}
}
func TestModelAStableRegardlessInputOrder(t *testing.T){
 a:=modelA("install-aaa","scope-one","aaa","install")
 b:=modelA("install-bbb","scope-one","bbb","install")
 pa,_:=BuildFleet([]Request{a,b})
 pb,_:=BuildFleet([]Request{b,a})
 if !reflect.DeepEqual(pa,pb){t.Fatal("nondeterministic install receipts")}
}

func TestModelASameKeyTwoScopesDeterministic(t *testing.T){
 r:=modelA("install-aaa","workspace-one","aaa","install")
 r.Desired=[]Resource{
  {Kind:Tunnel,Key:"tunnel-aaa",Version:"v1",Scope:"workspace-two"},
  {Kind:Tunnel,Key:"tunnel-aaa",Version:"v1",Scope:"workspace-one"},
 }
 first,e:=Build(r);if e!=nil{t.Fatal(e)}
 if len(first.Steps)!=2||first.Steps[0].Scope!="workspace-one"||first.Steps[1].Scope!="workspace-two"{
  t.Fatal("scope-distinct duplicate names not sorted by scope")
 }
 for i:=0;i<25;i++{
  next,e:=Build(r);if e!=nil||!reflect.DeepEqual(first,next){t.Fatal("map order leaked into plan")}
 }
}
