package bootstrap
import (
 "context"
 "errors"
 "fmt"
 "strings"
 "testing"
)
const tunnelID="tunnel_0123456789abcdef0123456789abcdef"
type fake struct {commands []Command; output [][]byte; failAt int}
func (f *fake)Run(_ context.Context,c Command)([]byte,error){
 f.commands=append(f.commands,c)
 if f.failAt>0 && len(f.commands)==f.failAt{return nil,errors.New("fake sensitive upstream message")}
 if len(f.output)<len(f.commands){return nil,errors.New("missing fake output")}
 return f.output[len(f.commands)-1],nil
}
func base()Config{return Config{
 Alias:"suprachat-rdte",Endpoint:"http://127.0.0.1:18761/mcp",
 TunnelID:tunnelID,RuntimeKeyPresent:true,Execute:true}}
func success() *fake{return &fake{output:[][]byte{
 []byte(`{"alias":"suprachat-rdte","mode":"managed"}`),
 []byte(`{"alias":"suprachat-rdte","tunnel_id":"`+tunnelID+`","runtime_state":"running","healthy":true,"ready":true,"process_running":true}`),
 []byte(`{"installed":true}`),[]byte(`{"plugin_installed":true}`),
}}}
func TestPlanAndAdmission(t *testing.T){
 c:=base();c.Execute=false;c.RuntimeKeyPresent=false
 p,e:=PlanFor(c);if e!=nil{t.Fatal(e)}
 if !p.TunnelReuse||p.TunnelCreateRequired||p.ChatGPTConnectorCreated||p.ChatGPTAccountAccessVerified||p.Execute{t.Fatal("invalid dry plan")}
 cases:=[]struct{name string;edit func(*Config)}{
  {"missing_reuse_id",func(c *Config){c.TunnelID=""}},
  {"creation_requires_scope",func(c *Config){c.TunnelID="";c.AllowCreate=true}},
  {"creation_requires_admin_key",func(c *Config){c.TunnelID="";c.AllowCreate=true;c.WorkspaceID="ws_123";c.AdminKeyPresent=false}},
  {"invalid_tunnel",func(c *Config){c.TunnelID="invalid"}},
  {"unsafe_alias",func(c *Config){c.Alias="../other"}},
  {"remote_endpoint",func(c *Config){c.Endpoint="https://example.com/mcp"}},
  {"localhost_suffix",func(c *Config){c.Endpoint="http://localhost.evil.invalid:18761/mcp"}},
  {"wrong_path",func(c *Config){c.Endpoint="http://127.0.0.1:18761/admin"}},
  {"embedded_auth",func(c *Config){c.Endpoint="http://user:pass@127.0.0.1:18761/mcp"}},
  {"query",func(c *Config){c.Endpoint="http://127.0.0.1:18761/mcp?token=secret"}},
  {"no_port",func(c *Config){c.Endpoint="http://127.0.0.1/mcp"}},
  {"invalid_org",func(c *Config){c.OrganizationID="../../secret"}},
  {"missing_runtime",func(c *Config){c.RuntimeKeyPresent=false}},
 }
 for _,tc:=range cases{t.Run(tc.name,func(t *testing.T){
  x:=base();tc.edit(&x);if e:=Validate(x);e==nil{t.Fatal("unsafe configuration accepted")}
 })}
 c=base();c.TunnelID="";c.AllowCreate=true;c.WorkspaceID="ws_123";c.AdminKeyPresent=true
 if _,e:=PlanFor(c);e!=nil{t.Fatal("valid scoped creation refused")}
}
func TestExistingTunnelManagedReady(t *testing.T){
 f:=success();res,e:=Apply(context.Background(),base(),f)
 if e!=nil{t.Fatal(e)}
 if !res.RuntimeHealthy||!res.RuntimeReady||!res.ProcessRunning||res.ChatGPTConnectorCreated||res.AuthenticatedWebUIObserved {t.Fatal("unsupported acceptance claim")}
 if len(f.commands)!=2 || f.commands[0].Admin{t.Fatal("unnecessary admin permissions")}
 args:=strings.Join(f.commands[0].Args," ")
 for _,s:=range []string{"runtimes --json connect","--tunnel-id "+tunnelID,"--runtime-api-key env:CONTROL_PLANE_API_KEY","--mcp-server-url http://127.0.0.1:18761/mcp"} {
  if !strings.Contains(args,s){t.Fatalf("upstream command missing %s",s)}
 }
}
func TestScopedCreationAndCodexPlugin(t *testing.T){
 c:=base();c.TunnelID="";c.AllowCreate=true;c.AdminKeyPresent=true;c.WorkspaceID="ws_123";c.InstallCodex=true
 f:=success();res,e:=Apply(context.Background(),c,f)
 if e!=nil{t.Fatal(e)}
 if !res.CodexPluginVerified||len(f.commands)!=4||!f.commands[0].Admin{t.Fatal("create/plugin not qualified")}
 cmd:=strings.Join(f.commands[0].Args," ")
 for _,s:=range []string{"--workspace-id ws_123","--admin-key env:OPENAI_ADMIN_KEY","--name SupraChat RDTE"}{
  if !strings.Contains(cmd,s){t.Fatalf("creation missing %s",s)}
 }
 for _,arg:=range f.commands[1].Args{if strings.Contains(arg,"ADMIN_KEY"){t.Fatal("admin key leaked into status command")}}
}
func TestRefuseUnknownAndPartialRuntimeState(t *testing.T){
 cases:=[]struct{name string;mutate func(*fake)}{
  {"connect_fail",func(f *fake){f.failAt=1}},
  {"status_fail",func(f *fake){f.failAt=2}},
  {"wrong_alias",func(f *fake){f.output[1]=[]byte(`{"alias":"wrong","tunnel_id":"`+tunnelID+`","healthy":true,"ready":true,"process_running":true}`)}},
  {"wrong_tunnel",func(f *fake){f.output[1]=[]byte(`{"alias":"suprachat-rdte","tunnel_id":"`+strings.Repeat("x",32)+`","healthy":true,"ready":true,"process_running":true}`)}},
  {"stale",func(f *fake){f.output[1]=[]byte(`{"alias":"suprachat-rdte","tunnel_id":"`+tunnelID+`","healthy":true,"ready":false,"process_running":true}`)}},
  {"not_running",func(f *fake){f.output[1]=[]byte(`{"alias":"suprachat-rdte","tunnel_id":"`+tunnelID+`","healthy":true,"ready":true,"process_running":false}`)}},
  {"error_doc",func(f *fake){f.output[1]=[]byte(`{"error":{"message":"SECRET_UPSTREAM"}}`)}},
  {"bad_json",func(f *fake){f.output[1]=[]byte("{")}},
  {"oversized",func(f *fake){f.output[1]=[]byte(strings.Repeat("x",33000))}},
 }
 for _,tc:=range cases{t.Run(tc.name,func(t *testing.T){
  f:=success();tc.mutate(f)
  res,e:=applyWithWait(context.Background(),base(),f,func(context.Context)error{return errors.New("simulated deadline")})
  if e==nil{t.Fatal("broken runtime accepted")}
  if res.ChatGPTConnectorCreated||res.AuthenticatedWebUIObserved{t.Fatal("fabricated connectivity")}
  if strings.Contains(fmt.Sprint(e),"SECRET_UPSTREAM")||strings.Contains(fmt.Sprint(e),"sensitive upstream"){t.Fatal("secret exposed in error")}
 })}
}
func TestOptionalPluginMustBeVerified(t *testing.T){
 c:=base();c.InstallCodex=true;f:=success()
 f.output[3]=[]byte(`{"plugin_installed":false}`)
 if _,e:=Apply(context.Background(),c,f);e==nil{t.Fatal("unverified Codex install accepted")}
}
func TestStopRequiresExplicitExecution(t *testing.T){
 c:=base();c.Execute=false;f:=success()
 if e:=Stop(context.Background(),c,f);e==nil{t.Fatal("implicit stop")}
 c.Execute=true
 f.output[0]=[]byte(`{"alias":"suprachat-rdte","stopped":true,"process_running":false,"ready":false}`)
 if e:=Stop(context.Background(),c,f);e!=nil{t.Fatal(e)}
 if len(f.commands)!=1||strings.Join(f.commands[0].Args," ")!="runtimes --json stop suprachat-rdte"{t.Fatal("unexpected stop")}
}

func TestNewTunnelConvergesOnlyAfterIndependentReadyStatus(t *testing.T){
 c:=base();f:=success()
 f.output=[][]byte{
  []byte(`{"alias":"suprachat-rdte","mode":"managed"}`),
  []byte(`{"alias":"suprachat-rdte","tunnel_id":"`+tunnelID+`","healthy":false,"ready":false,"process_running":true}`),
  []byte(`{"alias":"suprachat-rdte","tunnel_id":"`+tunnelID+`","healthy":true,"ready":false,"process_running":true}`),
  []byte(`{"alias":"suprachat-rdte","tunnel_id":"`+tunnelID+`","healthy":true,"ready":true,"process_running":true}`),
 }
 waits:=0
 res,err:=applyWithWait(context.Background(),c,f,func(context.Context)error{waits++;return nil})
 if err!=nil{t.Fatal(err)}
 if !res.RuntimeHealthy||!res.RuntimeReady||!res.ProcessRunning||waits!=2||len(f.commands)!=4{t.Fatal("premature success or retry mismatch")}
}
func TestBoundedReadinessNeverWaived(t *testing.T){
 c:=base();f:=success()
 notReady:=[]byte(`{"alias":"suprachat-rdte","tunnel_id":"`+tunnelID+`","healthy":true,"ready":false,"process_running":true}`)
 f.output=[][]byte{[]byte(`{"alias":"suprachat-rdte","mode":"managed"}`)}
 for i:=0;i<8;i++{f.output=append(f.output,notReady)}
 waits:=0
 res,err:=applyWithWait(context.Background(),c,f,func(context.Context)error{waits++;return nil})
 if err==nil||res.RuntimeReady||res.ChatGPTConnectorCreated||waits!=7||len(f.commands)!=9{t.Fatal("unready runtime was promoted or loop exceeded bound")}
}

func TestStopRejectsUnverifiedTeardown(t *testing.T){
 cases:=[]struct{name,body string}{
  {"false_stopped","{\"alias\":\"suprachat-rdte\",\"stopped\":false,\"process_running\":false,\"ready\":false}"},
  {"still_running","{\"alias\":\"suprachat-rdte\",\"stopped\":true,\"process_running\":true,\"ready\":false}"},
  {"still_ready","{\"alias\":\"suprachat-rdte\",\"stopped\":true,\"process_running\":false,\"ready\":true}"},
  {"different_alias","{\"alias\":\"another-runtime\",\"stopped\":true,\"process_running\":false,\"ready\":false}"},
  {"missing_status","{\"alias\":\"suprachat-rdte\",\"stopped\":true}"},
 }
 for _,tc:=range cases{t.Run(tc.name,func(t *testing.T){
  c:=base();f:=success();f.output[0]=[]byte(tc.body)
  if e:=Stop(context.Background(),c,f);e==nil{t.Fatal("false teardown accepted")}
 })}
}
