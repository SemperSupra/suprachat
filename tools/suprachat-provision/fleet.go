package provision

import (
 "errors"
 "sort"
)

// FleetPlan qualifies multiple independent installation *plans*, not a shared
// hosted gateway. Each installation remains its own MCP provider and tunnel.
// No credentials, real account state or remote APIs are read by this planner.
const FleetSchema="suprachat-model-a-fleet-plan/v1"
type FleetPlan struct {
 Schema string `json:"schema"`
 DryRun bool `json:"dry_run"`
 ChangesApplied bool `json:"changes_applied"`
 ChatGPTConnected bool `json:"chatgpt_connected"`
 Architecture string `json:"architecture"`
 Installations []Plan `json:"installations"`
 Status string `json:"status"`
}

// Enforce separate installation identities, connection-specific resource keys
// within each scope and nonshared remote tunnel IDs. A portable plugin *product*
// may be the same version, but registration keys denote separate connections.
func BuildFleet(requests []Request)(FleetPlan,error){
 result:=FleetPlan{Schema:FleetSchema,DryRun:true,Architecture:"model_a_independent_private_tunnels",
  Installations:[]Plan{},Status:"NOT_APPLIED"}
 if len(requests)==0 || len(requests)>32{return FleetPlan{},errors.New("fleet size outside bounded MVP")}
 installIDs:=map[string]struct{}{}
 connectionKeys:=map[string]string{}
 tunnelIDs:=map[string]string{}
 all:=append([]Request(nil),requests...)
 sort.Slice(all,func(i,j int)bool{return all[i].InstallID<all[j].InstallID})
 for _,r:=range all{
  if _,exists:=installIDs[r.InstallID];exists{return FleetPlan{},errors.New("duplicate installation ID")}
  installIDs[r.InstallID]=struct{}{}
  p,err:=Build(r);if err!=nil{return FleetPlan{},err}
  // Both desired and observed must respect namespace separation. An uninstall
  // must not silently adopt a remote connection owned by another installation.
  for _,resources:=range [][]Resource{r.Desired,r.Observed}{
   for _,x:=range resources{
    if x.Kind==Tunnel || x.Kind==Runtime || x.Kind==ChatGPT{
     key:=id(x)
     if owner,exists:=connectionKeys[key];exists && owner!=r.InstallID{
      return FleetPlan{},errors.New("cross-installation connection identity collision")
     }
     connectionKeys[key]=r.InstallID
    }
    if x.Kind==Tunnel && x.RemoteID!=""{
     if owner,exists:=tunnelIDs[x.RemoteID];exists && owner!=r.InstallID{
      return FleetPlan{},errors.New("same remote tunnel assigned to independent installations")
     }
     tunnelIDs[x.RemoteID]=r.InstallID
    }
   }
  }
  result.Installations=append(result.Installations,p)
  if p.Blocked>0{result.Status="BLOCKED_OR_OPERATOR_ACTION"}
 }
 return result,nil
}
