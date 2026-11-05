package provision
import (
 "encoding/json"
 "os"
 "reflect"
 "testing"
)
func fixture(t *testing.T,name string)Request{
 t.Helper();b,e:=os.ReadFile("fixtures/"+name);if e!=nil{t.Fatal(e)}
 var r Request;if e=json.Unmarshal(b,&r);e!=nil{t.Fatal(e)};return r
}
func operation(p Plan,k Kind)string{for _,s:=range p.Steps{if s.Kind==k{return s.Operation}};return ""}
func TestInstallDryRunAndUIHold(t *testing.T){
 p,e:=Build(fixture(t,"install.json"));if e!=nil{t.Fatal(e)}
 if !p.DryRun||p.ChangesApplied||p.ChatGPTConnected||p.Blocked!=1{t.Fatal("unsafe result")}
 if operation(p,Tunnel)!="create_owned_tunnel"||operation(p,ChatGPT)!="operator_register_chatgpt_plugin"{t.Fatal("false acceptance")}
}
func TestUninstallAttachedPreservesTunnel(t *testing.T){
 r:=fixture(t,"uninstall.json");p,e:=Build(r);if e!=nil{t.Fatal(e)}
 if operation(p,ChatGPT)!="operator_disconnect"||operation(p,Tunnel)!="defer_tunnel_delete"||operation(p,Runtime)!="stop_owned_runtime"{t.Fatal("teardown order violated")}
 r.Observed[2].Connected=false
 r.Observed[0].RemoteID="tunnel_fake"
 r.Observed[0].RecordedFingerprint=digest(r.Observed[0]);r.Observed[0].ObservedFingerprint=r.Observed[0].RecordedFingerprint
 p,_=Build(r)
 if operation(p,Tunnel)!="delete_owned_tunnel"{t.Fatal("detached owned tunnel not removable")}
}
func TestForeignOwnershipAndFingerprintDrift(t *testing.T){
 r:=fixture(t,"uninstall.json");r.Observed[0].Owner="other-installer"
 p,_:=Build(r);if operation(p,Tunnel)!="preserve_unowned"{t.Fatal("foreign deletion")}
 r.Observed[0].Owner=r.InstallID;r.Observed[0].RecordedFingerprint="a";r.Observed[0].ObservedFingerprint="b"
 p,_=Build(r);if operation(p,Tunnel)!="refuse_drift"{t.Fatal("drift deletion")}
}
func TestUpgradeUndoAndArchive(t *testing.T){
 r:=fixture(t,"install.json");r.Observed=[]Resource{{Kind:Runtime,Key:"suprachat-local",Version:"old-v1",Scope:"personal-workspace",Owner:r.InstallID}}
 p,_:=Build(r);if operation(p,Runtime)!="upgrade_owned"{t.Fatal("upgrade not represented")}
 r.Mode="uninstall";r.Observed=[]Resource{{Kind:Project,Key:"project-test",Version:"v1",Scope:"personal-workspace",Owner:r.InstallID}}
 r.Cap.PlatformAdmin=true;p,_=Build(r)
 if operation(p,Project)!="operator_archive_approval"{t.Fatal("unapproved project archive")}
 r.Cap.ArchiveApproved=true;p,_=Build(r)
 if operation(p,Project)!="archive_owned_project"{t.Fatal("project archive not available")}
}
func TestDeterminismAndBadInputs(t *testing.T){
 r:=fixture(t,"install.json");a,_:=Build(r)
 for i:=0;i<15;i++{b,e:=Build(r);if e!=nil||!reflect.DeepEqual(a,b){t.Fatal("nondeterministic")}}
 bad:=r;bad.Mode="purge-all";if _,e:=Build(bad);e==nil{t.Fatal("bad mode")}
 bad=r;bad.Desired=append(append([]Resource{},r.Desired...),r.Desired[0]);if _,e:=Build(bad);e==nil{t.Fatal("duplicate")}
 bad=r;bad.Desired=append([]Resource{},r.Desired...);bad.Desired[0].Key="../admin";if _,e:=Build(bad);e==nil{t.Fatal("unsafe id")}
 bad=r;bad.Desired=append([]Resource{},r.Desired...);bad.Desired[0].Kind="cookies";if _,e:=Build(bad);e==nil{t.Fatal("unknown kind")}
}
func TestPluginManifestAndMarketplace(t *testing.T){
 b,e:=os.ReadFile("../../plugins/suprachat/plugin.json");if e!=nil{t.Fatal(e)}
 var p struct{Name string `json:"name"`;Extensions map[string]json.RawMessage `json:"extensions"`}
 if json.Unmarshal(b,&p)!=nil||p.Name!="suprachat"||p.Extensions["com.openai"]==nil{t.Fatal("invalid portable plugin")}
 b,e=os.ReadFile("../../.agents/plugins/marketplace.json");if e!=nil{t.Fatal(e)}
 var m struct{Name string `json:"name"`;Plugins []struct{Name string `json:"name"`;Source struct{Path string `json:"path"`}} `json:"plugins"`}
 if json.Unmarshal(b,&m)!=nil||m.Name!="suprachat-local"||len(m.Plugins)!=1||m.Plugins[0].Source.Path!="./plugins/suprachat"{t.Fatal("invalid marketplace")}
}

func TestRemoteCleanupRequiresFreshMatchingIdentity(t *testing.T){
 r:=fixture(t,"uninstall.json");r.Observed[2].Connected=false
 p,_:=Build(r)
 if operation(p,Tunnel)!="verify_remote_owner"{t.Fatal("unverified remote tunnel deletion admitted")}
 r.Observed[0].RemoteID="tunnel_fake"
 r.Observed[0].RecordedFingerprint=digest(r.Observed[0]);r.Observed[0].ObservedFingerprint=r.Observed[0].RecordedFingerprint
 p,_=Build(r)
 if operation(p,Tunnel)!="delete_owned_tunnel"{t.Fatal("fresh verified owned tunnel not removable")}
 r.Observed[0].ObservedFingerprint="tampered"
 p,_=Build(r)
 if operation(p,Tunnel)!="refuse_drift"{t.Fatal("drift not fenced")}
}
