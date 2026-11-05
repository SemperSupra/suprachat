package admission

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
 provision "example.com/suprachat/provision"
)
func buildBin(t *testing.T,dir,target,name string)string{
 t.Helper()
 suffix:="";if runtime.GOOS=="windows"{suffix=".exe"}
 path:=filepath.Join(t.TempDir(),name+suffix)
 c:=exec.Command("go","build","-trimpath","-o",path,target);c.Dir=dir
 if result,e:=c.CombinedOutput();e!=nil{t.Fatalf("failed building real %s binary: %s",name,result)}
 return path
}
func syntheticEnv(alias,id,key string,admin bool)[]string{
 deny:=map[string]bool{"CONTROL_PLANE_API_KEY":true,"OPENAI_ADMIN_KEY":true,
 "OPENAI_API_KEY":true,"GITHUB_TOKEN":true,"GH_TOKEN":true,
 "SYNTHETIC_ALIAS":true,"SYNTHETIC_TUNNEL":true,"SYNTHETIC_RUNTIME":true}
 e:=[]string{}
 for _,s:=range os.Environ(){
  parts:=strings.SplitN(s,"=",2)
  if len(parts)==2&&!deny[strings.ToUpper(parts[0])]{e=append(e,s)}
 }
 e=append(e,"SYNTHETIC_ALIAS="+alias,"SYNTHETIC_TUNNEL="+id,
  "SYNTHETIC_RUNTIME="+key,"CONTROL_PLANE_API_KEY="+key,
  "OPENAI_API_KEY=PRIVATE_TOKEN_CANARY","GH_TOKEN=PRIVATE_GH_CANARY")
 if admin{e=append(e,"OPENAI_ADMIN_KEY=SYNTHETIC_ADMIN")}
 return e
}
func TestHandoffInvokesRealMergedTunnelctlAgainstTwoSyntheticOfficialClients(t *testing.T){
 wd,e:=os.Getwd();if e!=nil{t.Fatal(e)}
 wrapper:=buildBin(t,filepath.Join(wd,"..","oec-tunnel-bootstrap"),"./cmd/tunnelctl","actual-wrapper")
 fake:=buildBin(t,filepath.Join(wd,"..","oec-install-handoff"),"./cmd/fake-tunnel-client","fake-upstream")
 home:=fixture("install-home","home")
 work:=fixture("install-work","work")
 work.LocalMCPURL="http://127.0.0.1:18762/mcp"
 reuse:=work.Provision.Desired[0]
 reuse.Owner="install-work";reuse.RemoteID="tunnel_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
 work.Provision.Observed=[]provision.Resource{reuse}
 ownerPlan,e:=provision.Build(work.Provision);if e!=nil{t.Fatal(e)}
 for _,step:=range ownerPlan.Steps{if step.Kind==provision.Tunnel{
  work.Provision.Observed[0].RecordedFingerprint=step.CompareFingerprint
  work.Provision.Observed[0].ObservedFingerprint=step.CompareFingerprint
 }}
 type item struct {req Request; alias,id,key string;create bool}
 entries:=[]item{
  {home,"runtime-home","tunnel_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","SYNTHETIC_RUNTIME_HOME",true},
  {work,"runtime-work","tunnel_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","SYNTHETIC_RUNTIME_WORK",false},
 }
 var wg sync.WaitGroup
 failures:=make(chan string,len(entries))
 for _,x:=range entries{
  wg.Add(1)
  go func(x item){
   defer wg.Done()
   p,err:=For(x.req);if err!=nil{failures<-err.Error();return}
   cmd:=get(p,"platform")
   if has(cmd.Args,"--allow-create")!=x.create ||
     has(cmd.EnvRefs,"OPENAI_ADMIN_KEY")!=x.create ||
     has(cmd.Args,"--execute") || has(cmd.Args,"--binary") {
    failures<-"incorrect inert create vs reuse argv";return
   }
   args:=append(append([]string(nil),cmd.Args...),"--execute","--binary",fake)
   c:=exec.Command(wrapper,args...);c.Env=syntheticEnv(x.alias,x.id,x.key,x.create)
   var stdout,stderr bytes.Buffer;c.Stdout=&stdout;c.Stderr=&stderr
   if err=c.Run();err!=nil{failures<-"actual wrapper refused synthetic official client: "+stderr.String();return}
   var result map[string]any
   if json.Unmarshal(stdout.Bytes(),&result)!=nil||result["runtime_healthy"]!=true||
     result["runtime_ready"]!=true||result["process_running"]!=true||
     result["chatgpt_connector_created"]!=false||result["authenticated_webui_observed"]!=false{
    failures<-"inaccurate runtime or account receipt";return
   }
   for _,key:=range []string{"PRIVATE_TOKEN_CANARY","PRIVATE_GH_CANARY",x.key,"SYNTHETIC_ADMIN"}{
    if strings.Contains(stdout.String(),key)||strings.Contains(stderr.String(),key){
     failures<-"secret canary in output";return
    }
   }
  }(x)
 }
 wg.Wait();close(failures)
 for failure:=range failures{t.Error(failure)}
}
func TestActualAdmissionCLIRefusesUnknownAccountSecretsAndTrailingInput(t *testing.T){
 wd,e:=os.Getwd();if e!=nil{t.Fatal(e)}
 cli:=buildBin(t,wd,"./cmd/admit","admission")
 req:=fixture("install-home","home")
 good,e:=json.Marshal(req);if e!=nil{t.Fatal(e)}
 for _,body:=range [][]byte{
  append(append([]byte{},good...),[]byte("{}")...),
  []byte("{\"provision\":{\"mode\":\"install\",\"install_id\":\"install-home\"},\"api_key\":\"PRIVATE_TOKEN_CANARY\"}"),
 }{
  c:=exec.Command(cli);c.Stdin=bytes.NewReader(body)
  output,err:=c.CombinedOutput()
  if err==nil||bytes.Contains(output,[]byte("PRIVATE_TOKEN_CANARY")){t.Fatal("unknown credentials or trailing JSON accepted or disclosed")}
 }
 c:=exec.Command(cli);c.Stdin=bytes.NewReader(good)
 output,err:=c.CombinedOutput();if err!=nil{t.Fatalf("safe offline handoff rejected: %s",output)}
 var result Handoff
 if json.Unmarshal(output,&result)!=nil||!result.DryRun||result.ChangesApplied||
   result.ChatGPTPluginInstalled||result.AuthenticatedChatGPTVerified{
  t.Fatal("CLI falsely claimed live installation or account acceptance")
 }
}
