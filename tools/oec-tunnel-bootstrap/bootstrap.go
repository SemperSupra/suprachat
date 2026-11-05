// Package bootstrap composes official openai/tunnel-client primitives.
// It never handles browser profiles, cookies, ChatGPT login, or provider prompts.
package bootstrap

import (
 "context"
 "encoding/json"
 "errors"
 "fmt"
 "net"
 "net/url"
 "regexp"
 "strconv"
 "strings"
 "time"
)

const Evidence="OFFLINE_CLI_SIMULATION_ONLY"
var validTunnel=regexp.MustCompile("^tunnel_([a-z0-9]{4}_)?[a-z0-9]{32}$")
var validAlias=regexp.MustCompile("^[a-z][a-z0-9-]{1,48}$")
var validScope=regexp.MustCompile("^[A-Za-z][A-Za-z0-9_-]{2,127}$")

type Config struct {
 Alias string
 Endpoint string
 TunnelID string
 OrganizationID string
 WorkspaceID string
 AllowCreate bool
 InstallCodex bool
 Execute bool
 RuntimeKeyPresent bool
 AdminKeyPresent bool
}
type Command struct { Args []string; Admin bool }
type Plan struct {
 Schema string `json:"schema"`
 EvidenceClass string `json:"evidence_class"`
 Execute bool `json:"execute"`
 TunnelReuse bool `json:"tunnel_reuse"`
 TunnelCreateRequired bool `json:"tunnel_create_required"`
 RuntimeKeyPresent bool `json:"runtime_key_present"`
 AdminKeyPresent bool `json:"admin_key_present"`
 ChatGPTConnectorCreated bool `json:"chatgpt_connector_created"`
 ChatGPTAccountAccessVerified bool `json:"chatgpt_account_access_verified"`
 ActionNames []string `json:"actions"`
}
type Result struct {
 Schema string `json:"schema"`
 EvidenceClass string `json:"evidence_class"`
 RuntimeHealthy bool `json:"runtime_healthy"`
 RuntimeReady bool `json:"runtime_ready"`
 ProcessRunning bool `json:"process_running"`
 CodexPluginVerified bool `json:"codex_plugin_verified"`
 ChatGPTConnectorCreated bool `json:"chatgpt_connector_created"`
 AuthenticatedWebUIObserved bool `json:"authenticated_webui_observed"`
}
type Runner interface {
 Run(context.Context,Command)([]byte,error)
}

func endpointOK(input string) bool {
 u,e:=url.Parse(input)
 if e!=nil || u.Scheme!="http" || u.RawQuery!="" || u.Fragment!="" || u.User!=nil || u.Path!="/mcp" || u.RawPath!="" {return false}
 if u.Hostname()!="127.0.0.1" && u.Hostname()!="localhost"{return false}
 port,e:=strconv.Atoi(u.Port())
 return e==nil && port>=1 && port<=65535 && net.ParseIP(u.Hostname())!=nil || (e==nil && port>=1 && port<=65535 && u.Hostname()=="localhost")
}
func Validate(c Config) error {
 if !validAlias.MatchString(c.Alias){return errors.New("invalid local alias")}
 if !endpointOK(c.Endpoint){return errors.New("only fixed loopback MCP http /mcp endpoint admitted")}
 if c.TunnelID!="" && !validTunnel.MatchString(c.TunnelID){return errors.New("invalid tunnel id")}
 if c.OrganizationID!="" && !validScope.MatchString(c.OrganizationID){return errors.New("invalid organization scope")}
 if c.WorkspaceID!="" && !validScope.MatchString(c.WorkspaceID){return errors.New("invalid workspace scope")}
 if c.TunnelID=="" && !c.AllowCreate{return errors.New("tunnel reuse id missing; creation not admitted")}
 if c.AllowCreate && c.TunnelID=="" && c.OrganizationID=="" && c.WorkspaceID==""{return errors.New("new tunnel requires explicit organization or workspace scope")}
 if c.Execute && !c.RuntimeKeyPresent{return errors.New("scoped runtime key required for execution")}
 if c.Execute && c.AllowCreate && c.TunnelID=="" && !c.AdminKeyPresent{return errors.New("admin key required only for new tunnel create")}
 return nil
}
func PlanFor(c Config)(Plan,error){
 if err:=Validate(c);err!=nil{return Plan{},err}
 actions:=[]string{"validate-existing-tunnel-or-create-scoped","managed-connect","independent-runtime-status"}
 if c.InstallCodex{actions=append(actions,"install-upstream-codex-plugin","verify-upstream-codex-status")}
 return Plan{Schema:"suprachat-official-tunnel-plan/v1",EvidenceClass:Evidence,
  Execute:c.Execute,TunnelReuse:c.TunnelID!="",TunnelCreateRequired:c.TunnelID=="",
  RuntimeKeyPresent:c.RuntimeKeyPresent,AdminKeyPresent:c.AdminKeyPresent,
  ChatGPTConnectorCreated:false,ChatGPTAccountAccessVerified:false,
  ActionNames:actions},nil
}
func connectArgs(c Config)Command{
 args:=[]string{"runtimes","--json","connect","--alias",c.Alias,
  "--profile",c.Alias,"--mcp-server-url",c.Endpoint,
  "--runtime-api-key","env:CONTROL_PLANE_API_KEY"}
 admin:=false
 if c.TunnelID!="" {args=append(args,"--tunnel-id",c.TunnelID)
 } else {
  admin=true
  args=append(args,"--name","SupraChat RDTE "+c.Alias,
    "--description","Scoped SupraChat local synthetic observer RDTE")
  if c.OrganizationID!=""{args=append(args,"--organization-id",c.OrganizationID)}
  if c.WorkspaceID!=""{args=append(args,"--workspace-id",c.WorkspaceID)}
  args=append(args,"--admin-key","env:OPENAI_ADMIN_KEY")
 }
 return Command{Args:args,Admin:admin}
}
func decodeObject(raw []byte) (map[string]any,error){
 if len(raw)>32768{return nil,errors.New("unbounded upstream JSON")}
 var doc map[string]any
 if err:=json.Unmarshal(raw,&doc);err!=nil{return nil,errors.New("upstream JSON invalid")}
 if doc==nil || doc["error"]!=nil{return nil,errors.New("upstream failure reported")}
 return doc,nil
}
// Apply does not create a ChatGPT app. Runtime health is not the same thing
// as having a ChatGPT subscription that can attach a custom MCP connector.
func waitForRuntime(ctx context.Context)error{
 timer:=time.NewTimer(5*time.Second)
 defer timer.Stop()
 select {
 case <-ctx.Done():return ctx.Err()
 case <-timer.C:return nil
 }
}
func Apply(ctx context.Context,c Config,r Runner)(Result,error){
 return applyWithWait(ctx,c,r,waitForRuntime)
}
func applyWithWait(ctx context.Context,c Config,r Runner,wait func(context.Context)error)(Result,error){
 result:=Result{Schema:"suprachat-official-tunnel-result/v1",EvidenceClass:Evidence}
 if !c.Execute{return result,errors.New("apply requires explicit execute flag")}
 if _,e:=PlanFor(c);e!=nil{return result,e}
 raw,e:=r.Run(ctx,connectArgs(c))
 if e!=nil{return result,errors.New("upstream managed connect failed")}
 conn,e:=decodeObject(raw)
 if e!=nil{return result,e}
 if conn["alias"]!=c.Alias{return result,errors.New("connect alias mismatch")}
 // New Platform tunnels can require 25-30s propagation before ready.
 // Never classify a launched process as healthy without an exact-status proof.
 ready:=false
 for attempt:=0;attempt<8;attempt++{
  raw,e=r.Run(ctx,Command{Args:[]string{"runtimes","--json","status",c.Alias}})
  if e!=nil{return result,errors.New("upstream status unavailable")}
  status,e:=decodeObject(raw);if e!=nil{return result,e}
  if status["alias"]!=c.Alias{return result,errors.New("status alias mismatch")}
  id,ok:=status["tunnel_id"].(string)
  if !ok || !validTunnel.MatchString(id) || (c.TunnelID!="" && c.TunnelID!=id){return result,errors.New("unexpected tunnel id")}
  result.RuntimeHealthy=status["healthy"]==true
  result.RuntimeReady=status["ready"]==true
  result.ProcessRunning=status["process_running"]==true
  if result.RuntimeHealthy && result.RuntimeReady && result.ProcessRunning {ready=true;break}
  if attempt<7 {
   if err:=wait(ctx);err!=nil{return result,errors.New("managed runtime readiness deadline")}
  }
 }
 if !ready{return result,errors.New("managed runtime not independently healthy and ready")}
 if c.InstallCodex{
  if _,e=r.Run(ctx,Command{Args:[]string{"codex","plugin","install"}});e!=nil{return result,errors.New("upstream Codex plugin install failed")}
  raw,e=r.Run(ctx,Command{Args:[]string{"codex","status","--json"}})
  if e!=nil{return result,errors.New("upstream Codex plugin status unavailable")}
  cs,e:=decodeObject(raw);if e!=nil{return result,e}
  if cs["plugin_installed"]!=true{return result,errors.New("Codex plugin not verified installed")}
  result.CodexPluginVerified=true
 }
 return result,nil
}
func Stop(ctx context.Context,c Config,r Runner)error{
 if !c.Execute{return errors.New("stop requires execute flag")}
 if !validAlias.MatchString(c.Alias){return errors.New("invalid stop alias")}
 raw,e:=r.Run(ctx,Command{Args:[]string{"runtimes","--json","stop",c.Alias}})
 if e!=nil{return errors.New("upstream stop failed")}
 doc,e:=decodeObject(raw)
 if e!=nil{return e}
 // A successful process exit is not a completed teardown. Upstream v0.0.19
 // exposes alias/stopped/process_running/ready in its status payload.
 if doc["alias"]!=c.Alias || doc["stopped"]!=true ||
    doc["process_running"]!=false || doc["ready"]!=false {
  return errors.New("official managed runtime stop not independently confirmed")
 }
 return nil
}
// Errors from untrusted CLI output are intentionally not printed because they
// may include local paths, env fragments or remote response metadata.
func SafeString(s string)string{return strings.ReplaceAll(fmt.Sprintf("%s",s),"\n"," ")}
