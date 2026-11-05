package provision

import (
 "crypto/sha256"
 "encoding/hex"
 "errors"
 "fmt"
 "regexp"
 "sort"
)
// All output is a dry-run proposal; no real account credential or write is used.
const Schema="suprachat-provision-plan/v1"
type Kind string
const (
 Project Kind="platform_project"
 Tunnel Kind="platform_tunnel"
 Runtime Kind="local_runtime"
 Marketplace Kind="local_marketplace"
 Codex Kind="codex_plugin"
 ChatGPT Kind="chatgpt_plugin"
 APISkill Kind="api_skill"
)
var rank=map[Kind]int{Project:1,Tunnel:2,Runtime:3,Marketplace:4,Codex:5,APISkill:6,ChatGPT:7}
var safe=regexp.MustCompile("^[a-z0-9][a-z0-9_.:-]{2,95}$")
var versionRE=regexp.MustCompile("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$")
type Resource struct {
 Kind Kind `json:"kind"`
 Key string `json:"key"`
 Version string `json:"version"`
 Scope string `json:"scope"`
 RemoteID string `json:"remote_id,omitempty"`
 Owner string `json:"owner,omitempty"`
 RecordedFingerprint string `json:"recorded_fingerprint,omitempty"`
 ObservedFingerprint string `json:"observed_fingerprint,omitempty"`
 Connected bool `json:"connected,omitempty"`
 DetachedVerified bool `json:"detached_verified,omitempty"`
}
type Capabilities struct {
 PlatformAdmin bool `json:"platform_admin"`
 TunnelManage bool `json:"tunnel_manage"`
 RuntimeKey bool `json:"runtime_key"`
 CodexCLI bool `json:"codex_cli"`
 ProjectKey bool `json:"project_key"`
 ArchiveApproved bool `json:"archive_approved"`
}
type Request struct {
 Mode string `json:"mode"`
 InstallID string `json:"install_id"`
 Desired []Resource `json:"desired"`
 Observed []Resource `json:"observed"`
 Cap Capabilities `json:"capabilities"`
}
type Step struct {
 Kind Kind `json:"kind"`
 Scope string `json:"scope"`
 Key string `json:"key"`
 Operation string `json:"operation"`
 Undo string `json:"undo,omitempty"`
 RequiresUser bool `json:"requires_user"`
 BlockedReason string `json:"blocked_reason,omitempty"`
 CompareFingerprint string `json:"compare_fingerprint,omitempty"`
}
type Plan struct {
 InstallID string `json:"install_id"`
 Schema string `json:"schema"`
 DryRun bool `json:"dry_run"`
 Mode string `json:"mode"`
 Status string `json:"status"`
 Steps []Step `json:"steps"`
 Blocked int `json:"blocked"`
 ChangesApplied bool `json:"changes_applied"`
 ChatGPTConnected bool `json:"chatgpt_connected"`
}
// Scope is part of resource identity: independently scoped customers may reuse a key.
func id(x Resource)string{return string(x.Kind)+"/"+x.Scope+"/"+x.Key}
func valid(x Resource)error{
 if _,ok:=rank[x.Kind];!ok{return errors.New("unknown resource kind")}
 if !safe.MatchString(x.Key)||!versionRE.MatchString(x.Version)||!safe.MatchString(x.Scope){return errors.New("invalid identity")}
 if x.Owner!=""&&!safe.MatchString(x.Owner){return errors.New("invalid owner")}
 if x.RemoteID!=""&&!safe.MatchString(x.RemoteID){return errors.New("invalid remote id")}
 if x.Connected&&x.Kind!=ChatGPT&&x.Kind!=Runtime{return errors.New("invalid connection flag")}
 if x.DetachedVerified && (x.Kind!=ChatGPT||x.Connected){return errors.New("invalid detached attestation")}
 return nil
}
func digest(x Resource)string{
 sum:=sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%s/%s/%s/%s",x.Kind,x.Key,x.Scope,x.Version,x.RemoteID,x.Owner)))
 return hex.EncodeToString(sum[:])
}
// Remote deletion and upgrade require both the recorded claim and a fresh
// independently observed resource fingerprint from the executor.
func verifiedRemote(x Resource) bool {
 return x.RemoteID!="" && x.RecordedFingerprint!="" &&
   x.RecordedFingerprint==x.ObservedFingerprint && x.RecordedFingerprint==digest(x)
}
func Build(req Request)(Plan,error){
 p:=Plan{Schema:Schema,InstallID:req.InstallID,DryRun:true,Mode:req.Mode,Status:"NOT_APPLIED",Steps:[]Step{}}
 if req.Mode!="install"&&req.Mode!="uninstall"{return Plan{},errors.New("invalid mode")}
 if !safe.MatchString(req.InstallID){return Plan{},errors.New("invalid install id")}
 desired:=map[string]Resource{};observed:=map[string]Resource{}
 for _,x:=range req.Desired{
  if err:=valid(x);err!=nil{return Plan{},err}
  if _,ok:=desired[id(x)];ok{return Plan{},errors.New("duplicate desired")}
  desired[id(x)]=x
 }
 attached:=map[string]bool{}
 for _,x:=range req.Observed{
  if err:=valid(x);err!=nil{return Plan{},err}
  if _,ok:=observed[id(x)];ok{return Plan{},errors.New("duplicate observed")}
  observed[id(x)]=x
  if x.Kind==ChatGPT&&(x.Connected||!x.DetachedVerified){attached[x.Scope]=true}
 }
 items:=make([]Resource,0)
 if req.Mode=="install"{for _,x:=range desired{items=append(items,x)}}else{for _,x:=range observed{items=append(items,x)}}
 sort.Slice(items,func(i,j int)bool{
  a,b:=rank[items[i].Kind],rank[items[j].Kind]
  if a!=b {if req.Mode=="uninstall"{return a>b};return a<b}
  if items[i].Key!=items[j].Key{return items[i].Key<items[j].Key}
  return items[i].Scope<items[j].Scope
 })
 for _,x:=range items{
  s:=Step{Kind:x.Kind,Key:x.Key,Scope:x.Scope}
  old,exists:=observed[id(x)]
  if exists&&old.RecordedFingerprint!=""&&old.ObservedFingerprint!=""&&old.RecordedFingerprint!=old.ObservedFingerprint {
   s.Operation="refuse_drift";s.BlockedReason="fingerprint changed";p.Steps=append(p.Steps,s);continue
  }
  if req.Mode=="uninstall"{
   if old.Owner!=req.InstallID{s.Operation="preserve_unowned";p.Steps=append(p.Steps,s);continue}
   s.CompareFingerprint=digest(old)
   switch x.Kind{
   case ChatGPT:
    if old.Connected{s.Operation="operator_disconnect";s.RequiresUser=true;s.BlockedReason="ChatGPT plugin removal requires authorized account action"} else if !old.DetachedVerified{s.Operation="operator_verify_detached";s.RequiresUser=true;s.BlockedReason="ChatGPT plugin detachment not independently confirmed"} else{s.Operation="verify_detached"}
   case Tunnel:
    if attached[x.Scope]{s.Operation="defer_tunnel_delete";s.BlockedReason="ChatGPT plugin still attached"} else if !verifiedRemote(old){s.Operation="verify_remote_owner";s.BlockedReason="missing or drifted remote identity proof"} else if !req.Cap.TunnelManage{s.Operation="needs_tunnel_manage";s.BlockedReason="missing scope"} else{s.Operation="delete_owned_tunnel"}
   case Project:
    if !req.Cap.ArchiveApproved{s.Operation="operator_archive_approval";s.RequiresUser=true;s.BlockedReason="project only archives with approval"} else if !verifiedRemote(old){s.Operation="verify_remote_owner";s.BlockedReason="missing or drifted remote identity proof"} else if !req.Cap.PlatformAdmin{s.Operation="needs_platform_admin";s.BlockedReason="missing admin"} else{s.Operation="archive_owned_project"}
   case Runtime:s.Operation="stop_owned_runtime"
   case Marketplace:s.Operation="remove_owned_marketplace"
   case Codex:s.Operation="uninstall_owned_codex_plugin"
   case APISkill:
    if !verifiedRemote(old){s.Operation="verify_remote_owner";s.BlockedReason="missing or drifted remote identity proof"}else if req.Cap.ProjectKey{s.Operation="delete_owned_api_skill"}else{s.Operation="needs_project_key";s.BlockedReason="missing scope"}
   }
   p.Steps=append(p.Steps,s);continue
  }
  if exists{
   if old.Owner!=req.InstallID{s.Operation="preserve_unowned"} else if old.Version==x.Version{s.Operation="verify_owned_idempotent"} else if x.Kind==ChatGPT{s.Operation="operator_upgrade";s.RequiresUser=true;s.BlockedReason="ChatGPT plugin upgrade requires authorized account action"} else if (x.Kind==Tunnel||x.Kind==Project||x.Kind==APISkill)&&!verifiedRemote(old){s.Operation="verify_remote_owner";s.BlockedReason="missing or drifted remote identity proof"} else{s.Operation="upgrade_owned";s.Undo="restore_previous_version"}
   s.CompareFingerprint=digest(old);p.Steps=append(p.Steps,s);continue
  }
  switch x.Kind{
  case Project:
   if req.Cap.PlatformAdmin{s.Operation="create_owned_project";s.Undo="archive_created_project"} else{s.Operation="needs_platform_admin";s.BlockedReason="missing admin"}
  case Tunnel:
   if req.Cap.TunnelManage{s.Operation="create_owned_tunnel";s.Undo="delete_created_tunnel_if_detached"} else{s.Operation="needs_tunnel_manage";s.BlockedReason="missing scope"}
  case Runtime:
   if req.Cap.RuntimeKey{s.Operation="connect_owned_runtime";s.Undo="stop_owned_runtime"} else{s.Operation="needs_runtime_key";s.BlockedReason="missing scope"}
  case Marketplace:
   if req.Cap.CodexCLI{s.Operation="register_owned_marketplace";s.Undo="remove_owned_marketplace"} else{s.Operation="needs_codex_cli";s.BlockedReason="no supported local marketplace client"}
  case Codex:
   if req.Cap.CodexCLI{s.Operation="install_owned_codex_plugin";s.Undo="uninstall_owned_codex_plugin"} else{s.Operation="needs_codex_cli";s.BlockedReason="no supported plugin client"}
  case APISkill:
   if req.Cap.ProjectKey{s.Operation="upload_owned_api_skill";s.Undo="delete_owned_api_skill"} else{s.Operation="needs_project_key";s.BlockedReason="missing API skill scope"}
  case ChatGPT:
   s.Operation="operator_register_chatgpt_plugin";s.RequiresUser=true;s.BlockedReason="no verified unattended ChatGPT custom plugin install API"
  }
  p.Steps=append(p.Steps,s)
 }
 for _,s:=range p.Steps{if s.BlockedReason!=""{p.Blocked++}}
 if p.Blocked>0{p.Status="BLOCKED_OR_OPERATOR_ACTION"}else if len(p.Steps)==0{p.Status="NOOP"}
 return p,nil
}
