// Command tunnelctl is a narrow Go wrapper around official openai/tunnel-client.
// It does not itself implement a tunnel, MCP server, browser bridge, or ChatGPT app.
package main
import (
 "bytes"
 "context"
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "os/exec"
 "path/filepath"
 "slices"
 "strings"
 "time"
 bootstrap "example.com/suprachat/oec-tunnel-bootstrap"
)
type cliRunner struct{binary string}
// Child receives the runtime credential only when the upstream managed runtime
// needs it. Codex plugin commands and local stop never inherit API keys.
func sanitizedChildEnv(parent []string,c bootstrap.Command)[]string{
 env:=make([]string,0,len(parent))
 deny:=map[string]bool{
  "OPENAI_ADMIN_KEY":true,"CONTROL_PLANE_API_KEY":true,
  "OPENAI_API_KEY":true,"GITHUB_TOKEN":true,"GH_TOKEN":true,
  "CONTROL_PLANE_TUNNEL_ID":true,"MCP_SERVER_URL":true,
 }
 for _,v:=range parent{
  parts:=strings.SplitN(v,"=",2)
  if len(parts)!=2 || deny[strings.ToUpper(parts[0])]{continue}
  env=append(env,v)
 }
 needsRuntime:=len(c.Args)>0 && c.Args[0]=="runtimes" && !slices.Contains(c.Args,"stop")
 if needsRuntime && os.Getenv("CONTROL_PLANE_API_KEY")!=""{
  env=append(env,"CONTROL_PLANE_API_KEY="+os.Getenv("CONTROL_PLANE_API_KEY"))
 }
 if c.Admin && os.Getenv("OPENAI_ADMIN_KEY")!=""{
  env=append(env,"OPENAI_ADMIN_KEY="+os.Getenv("OPENAI_ADMIN_KEY"))
 }
 return env
}
func (r cliRunner) Run(ctx context.Context,c bootstrap.Command)([]byte,error){
 if len(c.Args)==0{return nil,errors.New("empty upstream command")}
 cmd:=exec.CommandContext(ctx,r.binary,c.Args...)
 cmd.Env=sanitizedChildEnv(os.Environ(),c)
 var stdout bytes.Buffer
 cmd.Stdout=&stdout
 cmd.Stderr=io.Discard
 if err:=cmd.Run();err!=nil{return nil,errors.New("upstream command refused")}
 if stdout.Len()>32768{return nil,errors.New("upstream output oversized")}
 return stdout.Bytes(),nil
}
func main(){
 alias:=flag.String("alias","suprachat-rdte","Local managed runtime alias")
 endpoint:=flag.String("mcp-url","http://127.0.0.1:18761/mcp","Explicit fixed localhost MCP target")
 tunnel:=flag.String("tunnel-id",os.Getenv("CONTROL_PLANE_TUNNEL_ID"),"Existing Platform tunnel ID")
 org:=flag.String("organization-id","","Optional explicit Platform org scope")
 workspace:=flag.String("workspace-id","","Optional explicit ChatGPT workspace scope")
 create:=flag.Bool("allow-create",false,"Explicitly permit creation if no tunnel ID was supplied")
 codex:=flag.Bool("install-codex-plugin",false,"Install and verify upstream Codex plugin (not ChatGPT)")
 action:=flag.String("action","plan","plan, connect, or stop")
 execute:=flag.Bool("execute",false,"Required for any mutation")
 binary:=flag.String("binary","","Absolute path to separately verified official tunnel-client")
 flag.Parse()
 c:=bootstrap.Config{
  Alias:*alias,Endpoint:*endpoint,TunnelID:*tunnel,
  OrganizationID:*org,WorkspaceID:*workspace,
  AllowCreate:*create,InstallCodex:*codex,Execute:*execute,
  RuntimeKeyPresent:os.Getenv("CONTROL_PLANE_API_KEY")!="",
  AdminKeyPresent:os.Getenv("OPENAI_ADMIN_KEY")!="",
 }
 out:=func(v any){if err:=json.NewEncoder(os.Stdout).Encode(v);err!=nil{os.Exit(2)}}
 if *action=="plan"{
  p,err:=bootstrap.PlanFor(c)
  if err!=nil{fmt.Fprintln(os.Stderr,"TUNNEL_PREFLIGHT_REFUSED");os.Exit(2)}
  out(p);return
 }
 if *action!="connect" && *action!="stop"{fmt.Fprintln(os.Stderr,"UNKNOWN_ACTION");os.Exit(2)}
 if !*execute{fmt.Fprintln(os.Stderr,"MUTATION_REQUIRES_EXECUTE");os.Exit(2)}
 if *binary=="" || !filepath.IsAbs(*binary){fmt.Fprintln(os.Stderr,"VERIFIED_ABSOLUTE_BINARY_PATH_REQUIRED");os.Exit(2)}
 if st,err:=os.Stat(*binary);err!=nil||st.IsDir(){fmt.Fprintln(os.Stderr,"UPSTREAM_BINARY_UNAVAILABLE");os.Exit(2)}
 runner:=cliRunner{binary:*binary}
 ctx,cancel:=context.WithTimeout(context.Background(),100*time.Second);defer cancel()
 if *action=="stop"{
  if err:=bootstrap.Stop(ctx,c,runner);err!=nil{fmt.Fprintln(os.Stderr,"TUNNEL_STOP_NOT_VERIFIED");os.Exit(1)}
  out(map[string]any{"schema":"suprachat-official-tunnel-stop/v1","stopped":true,"chatgpt_connector_removed":false});return
 }
 result,err:=bootstrap.Apply(ctx,c,runner)
 if err!=nil{fmt.Fprintln(os.Stderr,"TUNNEL_CONNECT_NOT_VERIFIED");os.Exit(1)}
 out(result)
}
