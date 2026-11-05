package handoff
import (
 "bytes"
 "encoding/json"
 "os"
 "os/exec"
 "path/filepath"
 "runtime"
 "strings"
 "sync"
 "testing"
)
const tunnelA="tunnel_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
const tunnelB="tunnel_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
func build(t *testing.T)(string,string){
 t.Helper();wd,e:=os.Getwd();if e!=nil{t.Fatal(e)}
 ext:="";if runtime.GOOS=="windows"{ext=".exe"}
 dir:=t.TempDir();cli:=filepath.Join(dir,"tunnelctl"+ext);fake:=filepath.Join(dir,"fake"+ext)
 for _,v:=range []struct{dir,out,path string}{
  {filepath.Join(wd,"..","oec-tunnel-bootstrap"),cli,"./cmd/tunnelctl"},
  {wd,fake,"./cmd/fake-tunnel-client"},
 }{
  c:=exec.Command("go","build","-trimpath","-o",v.out,v.path);c.Dir=v.dir
  if b,e:=c.CombinedOutput();e!=nil{t.Fatalf("could not build actual test executable: %s",b)}
 }
 return cli,fake
}
func env(items ...string)[]string{
 keys:=map[string]bool{
  "CONTROL_PLANE_API_KEY":true,"OPENAI_ADMIN_KEY":true,"OPENAI_API_KEY":true,
  "GITHUB_TOKEN":true,"GH_TOKEN":true,"SYNTHETIC_ALIAS":true,"SYNTHETIC_TUNNEL":true,
  "SYNTHETIC_RUNTIME":true,"SYNTHETIC_FAULT":true}
 out:=[]string{}
 for _,v:=range os.Environ(){
  p:=strings.SplitN(v,"=",2);if len(p)==2&&!keys[strings.ToUpper(p[0])]{out=append(out,v)}
 }
 return append(out,items...)
}
func command(binary string,envs []string,args ...string)(map[string]any,string,error){
 c:=exec.Command(binary,args...);c.Env=envs
 var out,stderr bytes.Buffer;c.Stdout=&out;c.Stderr=&stderr
 e:=c.Run();var data map[string]any
 if e==nil{e=json.Unmarshal(out.Bytes(),&data)}
 return data,stderr.String(),e
}
func testEnv(alias,id,key string)[]string{
 return env("CONTROL_PLANE_API_KEY="+key,"SYNTHETIC_RUNTIME="+key,"SYNTHETIC_ALIAS="+alias,
  "SYNTHETIC_TUNNEL="+id,"OPENAI_ADMIN_KEY=SYNTHETIC_ADMIN","OPENAI_API_KEY=UNSAFE_FAKE_API",
  "GITHUB_TOKEN=UNSAFE_FAKE_GITHUB")
}
func TestTwoConcurrentInstallationsAndTeardown(t *testing.T){
 cli,fake:=build(t)
 tests:=[]struct{alias,id,port,key string;codex bool}{
  {"home-private",tunnelA,"18611","SYNTHETIC_RUNTIME_HOME",false},
  {"work-private",tunnelB,"18612","SYNTHETIC_RUNTIME_WORK",true},
 }
 var wg sync.WaitGroup;errors:=make(chan string,len(tests))
 for _,x:=range tests{wg.Add(1);go func(x struct{alias,id,port,key string;codex bool}){
  defer wg.Done()
  e:=testEnv(x.alias,x.id,x.key)
  args:=[]string{"--action","connect","--execute","--binary",fake,
   "--alias",x.alias,"--tunnel-id",x.id,"--mcp-url","http://127.0.0.1:"+x.port+"/mcp"}
  if x.codex{args=append(args,"--install-codex-plugin")}
  d,stderr,err:=command(cli,e,args...)
  if err!=nil{errors<-x.alias+": initial connect refused "+stderr;return}
  if d["runtime_healthy"]!=true||d["runtime_ready"]!=true||d["process_running"]!=true||
     d["codex_plugin_verified"]!=x.codex||d["chatgpt_connector_created"]!=false||
     d["authenticated_webui_observed"]!=false{errors<-x.alias+": false receipt";return}
  s,st,err:=command(cli,e,"--action","stop","--execute","--binary",fake,"--alias",x.alias)
  if err!=nil||s["stopped"]!=true||s["chatgpt_connector_removed"]!=false{errors<-x.alias+": stop refused "+st;return}
 }(x)}
 wg.Wait();close(errors);for e:=range errors{t.Error(e)}
}
func TestDryRunAndRefusals(t *testing.T){
 cli,fake:=build(t)
 e:=env("OPENAI_API_KEY=UNSAFE_FAKE_API")
 p,_,err:=command(cli,e,"--action","plan","--tunnel-id",tunnelA)
 if err!=nil||p["chatgpt_connector_created"]!=false||p["execute"]!=false{t.Fatal("unsafe dry plan")}
 _,stderr,err:=command(cli,e,"--action","connect","--binary",fake,"--tunnel-id",tunnelA)
 if err==nil||strings.Contains(stderr,"UNSAFE_FAKE_API"){t.Fatal("implicit mutation or secret leak")}
 _,stderr,err=command(cli,e,"--action","connect","--execute","--binary",fake,"--tunnel-id",tunnelA)
 if err==nil||strings.Contains(stderr,"UNSAFE_FAKE_API"){t.Fatal("missing runtime key accepted or leaked")}
 e=testEnv("home-private",tunnelA,"SYNTHETIC_RUNTIME_HOME")
 _,stderr,err=command(cli,e,"--action","connect","--execute","--binary",fake,
  "--tunnel-id",tunnelA,"--alias","home-private","--mcp-url","https://untrusted.example/mcp")
 if err==nil||strings.Contains(stderr,"UNSAFE_FAKE_API"){t.Fatal("untrusted remote admitted")}
}
func TestScopedCreationAndMismatch(t *testing.T){
 cli,fake:=build(t)
 args:=[]string{"--action","connect","--execute","--binary",fake,"--alias","home-private",
 "--allow-create","--workspace-id","ws_fake","--mcp-url","http://127.0.0.1:18611/mcp"}
 e:=env("CONTROL_PLANE_API_KEY=SYNTHETIC_RUNTIME_HOME","SYNTHETIC_RUNTIME=SYNTHETIC_RUNTIME_HOME",
  "SYNTHETIC_ALIAS=home-private","SYNTHETIC_TUNNEL="+tunnelA)
 _,_,err:=command(cli,e,args...);if err==nil{t.Fatal("creation without admin accepted")}
 e=append(e,"OPENAI_ADMIN_KEY=SYNTHETIC_ADMIN")
 d,stderr,err:=command(cli,e,args...);if err!=nil||d["runtime_ready"]!=true{t.Fatalf("scoped creation contract refused: %s",stderr)}
 e=append(e,"SYNTHETIC_FAULT=wrong-tunnel")
 _,stderr,err=command(cli,e,"--action","connect","--execute","--binary",fake,
  "--alias","home-private","--tunnel-id",tunnelA)
 if err==nil||strings.Contains(stderr,"SYNTHETIC_ADMIN"){t.Fatal("mismatched tunnel accepted or admin leaked")}
}

func TestFakeUpstreamFalseStopCannotClaimCleanup(t *testing.T){
 cli,fake:=build(t)
 e:=append(testEnv("home-private",tunnelA,"SYNTHETIC_RUNTIME_HOME"),"SYNTHETIC_FAULT=false-stop")
 result,stderr,err:=command(cli,e,"--action","stop","--execute","--binary",fake,"--alias","home-private")
 if err==nil||result!=nil{t.Fatal("false upstream stop accepted")}
 if !strings.Contains(stderr,"TUNNEL_STOP_NOT_VERIFIED"){t.Fatal("failure was not classified as missing teardown proof")}
 if strings.Contains(stderr,"SYNTHETIC_ADMIN"){t.Fatal("credential echoed")}
}
