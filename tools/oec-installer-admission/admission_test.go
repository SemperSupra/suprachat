package admission
import (
 "encoding/json"
 "reflect"
 "strings"
 "testing"
 provision "example.com/suprachat/provision"
)
func fixture(id,device string)Request{
 scope:="workspace-alpha"
 return Request{
  Provision:provision.Request{Mode:"install",InstallID:id,
   Desired:[]provision.Resource{
    {Kind:provision.Tunnel,Scope:scope,Key:"tunnel-"+device,Version:"v1"},
    {Kind:provision.Runtime,Scope:scope,Key:"runtime-"+device,Version:"v1"},
    {Kind:provision.ChatGPT,Scope:scope,Key:"plugin-"+device,Version:"v1"},
   },
   Cap:provision.Capabilities{TunnelManage:true,RuntimeKey:true},
  },
  LocalMCPURL:"http://127.0.0.1:18761/mcp",PlatformWorkspaceID:"WorkspaceAlpha",
 }
}
func get(h Handoff,phase string)Step{
 for _,s:=range h.Steps{if s.Phase==phase{return s}}
 return Step{}
}
func has(ss []string,s string)bool{for _,x:=range ss{if x==s{return true}};return false}
func TestNewInstallNoCredentialAndNoDoubleCreate(t *testing.T){
 r:=fixture("install-home","home")
 h,e:=For(r);if e!=nil{t.Fatal(e)}
 if !h.DryRun||h.ChangesApplied||h.ChatGPTPluginInstalled||h.AuthenticatedChatGPTVerified||
    h.Status!="OPERATOR_AUTHORIZATION_AND_LIVE_READBACK_REQUIRED"{t.Fatal("invented account acceptance")}
 if len(h.Steps)!=5{t.Fatalf("unexpected phases: %+v",h.Steps)}
 step:=get(h,"platform")
 if step.Operation!="official_tunnelctl_connect_after_owner_authorization"||!has(step.Args,"--allow-create")||
    !has(step.Args,"--workspace-id")||has(step.Args,"--execute")||
    !has(step.EnvRefs,"CONTROL_PLANE_API_KEY")||!has(step.EnvRefs,"OPENAI_ADMIN_KEY"){
  t.Fatalf("wrong official managed create-connect handoff: %+v",step)
 }
 for _,s:=range h.Steps{
  if has(s.Args,"--execute")||has(s.Args,"--binary"){t.Fatal("credential-bearing live invocation created")}
 }
 b,_:=json.Marshal(h)
 for _,sentinel:=range []string{"SUPERSECRET","cookie","OPENAI_ADMIN_KEY=","CONTROL_PLANE_API_KEY="}{
  if strings.Contains(string(b),sentinel){t.Fatal("secret or credential value in emitted plan")}
 }
}
func TestTwoIndependentInstallationsSameWorkspace(t *testing.T){
 home:=fixture("install-home","home")
 work:=fixture("install-work","work")
 work.LocalMCPURL="http://127.0.0.1:18762/mcp"
 a,e:=Fleet([]Request{work,home});if e!=nil{t.Fatal(e)}
 b,e:=Fleet([]Request{home,work});if e!=nil||!reflect.DeepEqual(a,b){t.Fatal("nondeterministic fleet")}
 if len(a)!=2||a[0].InstallID!="install-home"||a[1].InstallID!="install-work"{t.Fatal("invalid stable install identity")}
 if reflect.DeepEqual(get(a[0],"platform").Args,get(a[1],"platform").Args){t.Fatal("installer tunnel aliases collapsed")}
}
func TestExistingTunnelRequiresFreshOwnershipFingerprint(t *testing.T){
 r:=fixture("install-home","home")
 tunnel:=r.Provision.Desired[0]
 tunnel.Owner=r.Provision.InstallID
 tunnel.RemoteID="tunnel_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
 r.Provision.Observed=[]provision.Resource{tunnel}
 if _,e:=For(r);e==nil{t.Fatal("adopted preexisting tunnel without record+observation proof")}
 p,e:=provision.Build(r.Provision);if e!=nil{t.Fatal(e)}
 for _,s:=range p.Steps{
  if s.Kind==provision.Tunnel{
   r.Provision.Observed[0].RecordedFingerprint=s.CompareFingerprint
   r.Provision.Observed[0].ObservedFingerprint=s.CompareFingerprint
  }
 }
 h,e:=For(r);if e!=nil{t.Fatal(e)}
 s:=get(h,"platform")
 if !has(s.Args,tunnel.RemoteID)||has(s.Args,"--allow-create")||has(s.EnvRefs,"OPENAI_ADMIN_KEY"){t.Fatal("existing tunnel rerouted into admin create")}
 r.Provision.Observed[0].ObservedFingerprint=strings.Repeat("f",64)
 if _,e:=For(r);e==nil{t.Fatal("drifted tunnel remotely adopted")}
}
func TestAttachedChatGPTStopsNothing(t *testing.T){
 r:=fixture("install-home","home")
 r.Provision.Mode="uninstall";r.Provision.Desired=nil
 r.Provision.Observed=[]provision.Resource{
  {Kind:provision.Tunnel,Scope:"workspace-alpha",Key:"tunnel-home",Version:"v1",
   Owner:"install-home",RemoteID:"tunnel_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
  {Kind:provision.Runtime,Scope:"workspace-alpha",Key:"runtime-home",Version:"v1",Owner:"install-home"},
  {Kind:provision.ChatGPT,Scope:"workspace-alpha",Key:"plugin-home",Version:"v1",
   Owner:"install-home",Connected:true},
 }
 h,e:=For(r);if e!=nil{t.Fatal(e)}
 if h.Status!="BLOCKED_CHATGPT_DETACHMENT"||len(h.Steps)!=1||h.Steps[0].Phase!="chatgpt"{t.Fatal("runtime stopped before ChatGPT detachment")}
 r.Provision.Observed[2].Connected=false
 h,e=For(r);if e!=nil{t.Fatal(e)}
 if h.Status!="BLOCKED_CHATGPT_DETACHMENT"{t.Fatal("unverified disconnection accepted")}
 r.Provision.Observed[2].DetachedVerified=true
 h,e=For(r);if e!=nil{t.Fatal(e)}
 if h.Status!="BLOCKED_TUNNEL_OWNER_PROOF"{t.Fatal("remote tunnel deleted without provenance")}
 p,_:=provision.Build(r.Provision)
 for _,s:=range p.Steps{if s.Kind==provision.Tunnel{
  r.Provision.Observed[0].RecordedFingerprint=s.CompareFingerprint
  r.Provision.Observed[0].ObservedFingerprint=s.CompareFingerprint
 }}
 h,e=For(r);if e!=nil{t.Fatal(e)}
 if h.Status!="BLOCKED_UNQUALIFIED_REMOTE_TEARDOWN_ADAPTER"||len(h.Steps)!=2{
  t.Fatal("issued unsafe half-uninstall without qualified journal and delete")
 }
 for _,s:=range h.Steps{if len(s.Args)>0{t.Fatal("produced destructive command without accepted journal")}}
}
func TestForeignAndDuplicateResourcesRefused(t *testing.T){
 r:=fixture("install-home","home")
 r.Provision.Desired[0].Owner="other-installer"
 if _,e:=For(r);e==nil{t.Fatal("foreign tunnel admitted")}
 r=fixture("install-home","home")
 r.Provision.Desired=append(r.Provision.Desired,r.Provision.Desired[0])
 if _,e:=For(r);e==nil{t.Fatal("duplicate resource")}
 r=fixture("install-home","home")
 r.Provision.Desired[1].Scope="another-workspace"
 if _,e:=For(r);e==nil{t.Fatal("cross workspace mixed identity")}
 r=fixture("install-home","home")
 r.Provision.Desired=r.Provision.Desired[:2]
 if _,e:=For(r);e==nil{t.Fatal("missing ChatGPT authorization handoff")}
}
func TestUpgradeAndUnsafeMCPRefused(t *testing.T){
 r:=fixture("install-home","home")
 r.LocalMCPURL="https://untrusted.example/mcp"
 if _,e:=For(r);e==nil{t.Fatal("off-loopback target admitted")}
 r=fixture("install-home","home")
 r.Provision.Observed=[]provision.Resource{
  {Kind:provision.Runtime,Scope:"workspace-alpha",Key:"runtime-home",Version:"v0",Owner:"install-home"},
 }
 if _,e:=For(r);e==nil{t.Fatal("unqualified upgrade admitted")}
}
func TestFleetRejectSameScopeAndRemoteCollision(t *testing.T){
 a:=fixture("install-aaa","same")
 b:=fixture("install-bbb","same")
 if _,e:=Fleet([]Request{a,b});e==nil{t.Fatal("shared tunnel identity accepted")}
 b=fixture("install-bbb","other")
 b.Provision.Observed=[]provision.Resource{{Kind:provision.Tunnel,Scope:"workspace-alpha",
   Key:"tunnel-other",Version:"v1",Owner:"install-bbb",RemoteID:"tunnel_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
 a.Provision.Observed=[]provision.Resource{{Kind:provision.Tunnel,Scope:"workspace-alpha",
   Key:"tunnel-same",Version:"v1",Owner:"install-aaa",RemoteID:"tunnel_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
 if _,e:=Fleet([]Request{a,b});e==nil{t.Fatal("shared physical tunnel admitted")}
}
func TestNoSecretOrLiveExecutorClaimsAcrossModes(t *testing.T){
 r:=fixture("install-home","home")
 cases:=[]Request{r}
 u:=r;u.Provision.Mode="uninstall";u.Provision.Desired=nil
 u.Provision.Observed=[]provision.Resource{
  {Kind:provision.Tunnel,Scope:"workspace-alpha",Key:"tunnel-home",Version:"v1",Owner:"install-home"},
  {Kind:provision.Runtime,Scope:"workspace-alpha",Key:"runtime-home",Version:"v1",Owner:"install-home"},
  {Kind:provision.ChatGPT,Scope:"workspace-alpha",Key:"plugin-home",Version:"v1",Owner:"install-home",Connected:true},
 }
 cases=append(cases,u)
 for _,x:=range cases{
  h,e:=For(x);if e!=nil{t.Fatal(e)}
  if !h.DryRun||h.ChangesApplied||h.ChatGPTPluginInstalled||h.AuthenticatedChatGPTVerified{t.Fatal("unobserved account claims")}
 }
}
