// Package admission maps accepted ownership-scoped Model A plans into bounded,
// non-executable official-tunnel-client installation handoffs. It never uses a
// Platform key, a ChatGPT session, a shell or an unqualified lifecycle journal.
package admission

import (
 "errors"
 "fmt"
 "sort"
 bootstrap "example.com/suprachat/oec-tunnel-bootstrap"
 provision "example.com/suprachat/provision"
)

const Schema="suprachat-model-a-install-admission/v1"
type Request struct {
 Provision provision.Request `json:"provision"`
 LocalMCPURL string `json:"local_mcp_url"`
 PlatformWorkspaceID string `json:"platform_workspace_id,omitempty"`
 PlatformOrganizationID string `json:"platform_organization_id,omitempty"`
}
type Step struct {
 Phase string `json:"phase"`
 Operation string `json:"operation"`
 Args []string `json:"args,omitempty"`
 EnvRefs []string `json:"env_refs,omitempty"`
 RequiresOperator bool `json:"requires_operator"`
 RequiresReconciliation bool `json:"requires_reconciliation"`
 Note string `json:"note,omitempty"`
}
type Handoff struct {
 Schema string `json:"schema"`
 EvidenceClass string `json:"evidence_class"`
 InstallID string `json:"install_id"`
 Scope string `json:"scope"`
 Mode string `json:"mode"`
 Status string `json:"status"`
 DryRun bool `json:"dry_run"`
 ChangesApplied bool `json:"changes_applied"`
 ChatGPTPluginInstalled bool `json:"chatgpt_plugin_installed"`
 AuthenticatedChatGPTVerified bool `json:"authenticated_chatgpt_verified"`
 Steps []Step `json:"steps"`
}
func refuse(reason string)error{return errors.New("MODEL_A_ADMISSION_REFUSED: "+reason)}
func find(resources []provision.Resource,kind provision.Kind)[]provision.Resource{
 out:=[]provision.Resource{}
 for _,r:=range resources{if r.Kind==kind{out=append(out,r)}}
 return out
}
func planStep(p provision.Plan,kind provision.Kind)(provision.Step,bool){
 for _,s:=range p.Steps{if s.Kind==kind{return s,true}}
 return provision.Step{},false
}
func single(req Request, kind provision.Kind, observed bool)(provision.Resource,error){
 values:=req.Provision.Desired
 if observed{values=req.Provision.Observed}
 x:=find(values,kind)
 if len(x)!=1{return provision.Resource{},refuse(fmt.Sprintf("requires exactly one %s record",kind))}
 return x[0],nil
}
func baseline(req Request)(Handoff,provision.Plan,error){
 p,e:=provision.Build(req.Provision)
 if e!=nil{return Handoff{},provision.Plan{},refuse("underlying ownership plan invalid")}
 h:=Handoff{Schema:Schema,EvidenceClass:"OFFLINE_OWNED_MODEL_A_PLAN_NOT_LIVE",
  InstallID:req.Provision.InstallID,Mode:req.Provision.Mode,
  DryRun:true,ChangesApplied:false,ChatGPTPluginInstalled:false,
  AuthenticatedChatGPTVerified:false,Steps:[]Step{},Status:"REFUSED"}
 if req.Provision.Mode!="install"&&req.Provision.Mode!="uninstall"{return Handoff{},p,refuse("unsupported mode")}
 return h,p,nil
}
// For creates a safe handoff: every official CLI command intentionally lacks
// --execute and a binary path. The qualified runtime/OS executor must later
// attach the verified absolute binary and approved credentials, after owner
// acceptance and durable journal admission.
func For(req Request)(Handoff,error){
 h,p,err:=baseline(req);if err!=nil{return Handoff{},err}
 observed:=req.Provision.Mode=="uninstall"
 tunnel,err:=single(req,provision.Tunnel,observed);if err!=nil{return Handoff{},err}
 runtime,err:=single(req,provision.Runtime,observed);if err!=nil{return Handoff{},err}
 plugin,err:=single(req,provision.ChatGPT,observed);if err!=nil{return Handoff{},err}
 if tunnel.Scope!=runtime.Scope||runtime.Scope!=plugin.Scope{return Handoff{},refuse("per-install resources have different scopes")}
 h.Scope=tunnel.Scope
 if runtime.Owner!=""&&runtime.Owner!=h.InstallID{return Handoff{},refuse("runtime belongs to foreign installation")}
 if plugin.Owner!=""&&plugin.Owner!=h.InstallID{return Handoff{},refuse("plugin connection belongs to foreign installation")}
 if tunnel.Owner!=""&&tunnel.Owner!=h.InstallID{return Handoff{},refuse("tunnel belongs to foreign installation")}
 if p.Mode=="uninstall"{return uninstall(req,h,p,tunnel,runtime,plugin)}
 return install(req,h,p,tunnel,runtime,plugin)
}
func install(req Request,h Handoff,p provision.Plan,tunnel,runtime,plugin provision.Resource)(Handoff,error){
 if len(find(req.Provision.Observed,provision.Tunnel))>1||
   len(find(req.Provision.Observed,provision.Runtime))>1 ||
   len(find(req.Provision.Observed,provision.ChatGPT))>1 {
  return Handoff{},refuse("ambiguous observed installation inventory")
 }
 ts,ok:=planStep(p,provision.Tunnel);if !ok{return Handoff{},refuse("missing tunnel ownership step")}
 rs,ok:=planStep(p,provision.Runtime);if !ok{return Handoff{},refuse("missing runtime ownership step")}
 ps,ok:=planStep(p,provision.ChatGPT);if !ok{return Handoff{},refuse("missing ChatGPT registration step")}
 if ts.Scope!=h.Scope||rs.Scope!=h.Scope||ps.Scope!=h.Scope{return Handoff{},refuse("scoped plan differs from inventory")}
 if ps.Operation!="operator_register_chatgpt_plugin"&&ps.Operation!="verify_owned_idempotent"{
  return Handoff{},refuse("ChatGPT plugin admission not an explicit operator action")
 }
 if rs.Operation=="upgrade_owned"||ts.Operation=="upgrade_owned"||ps.Operation=="operator_upgrade"{
  return Handoff{},refuse("upgrades require accepted durable journal and rollback qualification")
 }
 if rs.Operation!="connect_owned_runtime"&&rs.Operation!="verify_owned_idempotent"{
  return Handoff{},refuse("runtime cannot be safely connected")
 }
 cfg:=bootstrap.Config{
  Alias:runtime.Key,Endpoint:req.LocalMCPURL,
  OrganizationID:req.PlatformOrganizationID,WorkspaceID:req.PlatformWorkspaceID}
 switch ts.Operation{
 case "create_owned_tunnel":
  if tunnel.RemoteID!=""{return Handoff{},refuse("new tunnel unexpectedly carries remote ID")}
  cfg.AllowCreate=true
 case "verify_owned_idempotent":
  seen,err:=single(req,provision.Tunnel,true);if err!=nil{return Handoff{},err}
  if seen.Owner!=h.InstallID||seen.RemoteID==""||
    ts.CompareFingerprint==""||
    seen.RecordedFingerprint!=ts.CompareFingerprint||
    seen.ObservedFingerprint!=ts.CompareFingerprint{
   return Handoff{},refuse("existing tunnel lacks independently reconciled owner/fingerprint")
  }
  cfg.TunnelID=seen.RemoteID
 default:return Handoff{},refuse("tunnel not admitted for create or owned reuse")
 }
 // The bootstrap validator proves strict localhost /mcp, alias and Platform
 // scope syntax, and validates the actual official command's required fields.
 if _,err:=bootstrap.PlanFor(cfg);err!=nil{return Handoff{},refuse("official tunnel preflight failed")}
 argv:=[]string{"--action","connect","--alias",cfg.Alias,
  "--mcp-url",cfg.Endpoint}
 env:=[]string{"CONTROL_PLANE_API_KEY"}
 if cfg.TunnelID!=""{
  argv=append(argv,"--tunnel-id",cfg.TunnelID)
 }else{
  argv=append(argv,"--allow-create")
  if cfg.OrganizationID!=""{argv=append(argv,"--organization-id",cfg.OrganizationID)}
  if cfg.WorkspaceID!=""{argv=append(argv,"--workspace-id",cfg.WorkspaceID)}
  env=append(env,"OPENAI_ADMIN_KEY")
 }
 h.Steps=append(h.Steps,
  Step{Phase:"local",Operation:"independently_verify_local_mcp_health",RequiresReconciliation:true,
   Note:"No remote tunnel until loopback server identity, scope, and tool policy have passed"},
  Step{Phase:"platform",Operation:"official_tunnelctl_connect_after_owner_authorization",
   Args:argv,EnvRefs:env,RequiresOperator:true,RequiresReconciliation:true,
   Note:"Command is inert: no --execute or --binary. One managed connect creates or reuses a tunnel exactly once"},
  Step{Phase:"platform",Operation:"independent_runtime_status_and_tunnel_identity_readback",
   RequiresReconciliation:true,Note:"Must observe healthy, ready, running, exact alias and owned tunnel ID"},
  Step{Phase:"chatgpt",Operation:"authorized_chatgpt_plugin_registration_or_readback",
   RequiresOperator:true,RequiresReconciliation:true,Note:"No documented unattended ChatGPT account plugin registration API"},
  Step{Phase:"chatgpt",Operation:"authorized_tools_list_and_read_only_call",
   RequiresOperator:true,RequiresReconciliation:true,Note:"Authenticated account evidence required separately; do not claim it now"},
 )
 h.Status="OPERATOR_AUTHORIZATION_AND_LIVE_READBACK_REQUIRED"
 return h,nil
}
func uninstall(req Request,h Handoff,p provision.Plan,tunnel,runtime,plugin provision.Resource)(Handoff,error){
 if tunnel.Owner!=h.InstallID||runtime.Owner!=h.InstallID||plugin.Owner!=h.InstallID{
  return Handoff{},refuse("uninstall cannot delete unowned or missing-owner resources")
 }
 ts,ok:=planStep(p,provision.Tunnel);if !ok{return Handoff{},refuse("no remote tunnel disposal step")}
 rs,ok:=planStep(p,provision.Runtime);if !ok{return Handoff{},refuse("no runtime stop step")}
 ps,ok:=planStep(p,provision.ChatGPT);if !ok{return Handoff{},refuse("no ChatGPT detachment step")}
 if plugin.Connected||!plugin.DetachedVerified||ps.Operation!="verify_detached"{
  h.Status="BLOCKED_CHATGPT_DETACHMENT"
  h.Steps=append(h.Steps,Step{Phase:"chatgpt",Operation:"operator_disconnect_and_independently_verify",
   RequiresOperator:true,RequiresReconciliation:true,
   Note:"Do not stop local service or delete tunnel while ChatGPT connection may remain attached"})
  return h,nil
 }
 if ts.Operation!="delete_owned_tunnel"||ts.CompareFingerprint==""||
   tunnel.RecordedFingerprint!=ts.CompareFingerprint||
   tunnel.ObservedFingerprint!=ts.CompareFingerprint{
  h.Status="BLOCKED_TUNNEL_OWNER_PROOF"
  h.Steps=append(h.Steps,Step{Phase:"platform",Operation:"reconcile_tunnel_owner_and_remote_identity",
    RequiresOperator:true,RequiresReconciliation:true})
  return h,nil
 }
 if rs.Operation!="stop_owned_runtime"{return Handoff{},refuse("runtime teardown not admitted")}
 if _,e:=bootstrap.PlanFor(bootstrap.Config{Alias:runtime.Key,Endpoint:req.LocalMCPURL,
  TunnelID:tunnel.RemoteID});e!=nil{return Handoff{},refuse("official stop/remote identity preflight failed")}
 // The remote delete adapter is NOT yet qualified. Do not issue a half-uninstall:
 // require the owned journal and admin detached-tunnel deletion qualification.
 h.Status="BLOCKED_UNQUALIFIED_REMOTE_TEARDOWN_ADAPTER"
 h.Steps=append(h.Steps,
  Step{Phase:"chatgpt",Operation:"verify_chatgpt_connection_detached",RequiresReconciliation:true},
  Step{Phase:"platform",Operation:"qualify_owned_remote_tunnel_delete_and_journal",RequiresOperator:true,RequiresReconciliation:true,
    Note:"Stop/delete must be an accepted, owner-scoped transaction; do not remove local runtime prematurely"},
 )
 return h,nil
}
func Fleet(requests []Request)([]Handoff,error){
 if len(requests)==0||len(requests)>32{return nil,refuse("fleet outside bounded Model A admission")}
 raw:=make([]provision.Request,0,len(requests))
 byID:=map[string]Request{}
 for _,r:=range requests{
  raw=append(raw,r.Provision)
  if _,ok:=byID[r.Provision.InstallID];ok{return nil,refuse("duplicate installer identity")}
  byID[r.Provision.InstallID]=r
 }
 if _,e:=provision.BuildFleet(raw);e!=nil{return nil,refuse("cross-installation tunnel or connection collision")}
 keys:=make([]string,0,len(byID));for id:=range byID{keys=append(keys,id)};sort.Strings(keys)
 out:=make([]Handoff,0,len(keys))
 for _,id:=range keys{
  h,e:=For(byID[id]);if e!=nil{return nil,e}
  out=append(out,h)
 }
 return out,nil
}
