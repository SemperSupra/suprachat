// Public CI fixture only: never talks to OpenAI or real tunnels.
package main
import ("encoding/json";"fmt";"os";"strings")
func flag(args []string,k string)string{
 for i:=0;i+1<len(args);i++{if args[i]==k{return args[i+1]}};return ""
}
func refuse(){fmt.Fprintln(os.Stderr,"SYNTHETIC_REFUSAL");os.Exit(2)}
func main(){
 a:=os.Args[1:]
 if len(a)<3{refuse()}
 for _,s:=range []string{"OPENAI_API_KEY","GITHUB_TOKEN","GH_TOKEN"}{if os.Getenv(s)!=""{refuse()}}
 send:=func(v any){if json.NewEncoder(os.Stdout).Encode(v)!=nil{refuse()}}
 if a[0]=="runtimes"&&a[1]=="--json"{
  switch a[2]{
  case "connect":
   if flag(a,"--alias")!=os.Getenv("SYNTHETIC_ALIAS")||os.Getenv("CONTROL_PLANE_API_KEY")!=os.Getenv("SYNTHETIC_RUNTIME"){refuse()}
   if !strings.HasPrefix(flag(a,"--mcp-server-url"),"http://127.0.0.1:"){refuse()}
   id:=flag(a,"--tunnel-id")
   if id==""{
    if flag(a,"--admin-key")!="env:OPENAI_ADMIN_KEY"||os.Getenv("OPENAI_ADMIN_KEY")!="SYNTHETIC_ADMIN"{refuse()}
   }else if os.Getenv("OPENAI_ADMIN_KEY")!=""{refuse()}
   send(map[string]any{"alias":os.Getenv("SYNTHETIC_ALIAS")})
  case "status":
   if a[len(a)-1]!=os.Getenv("SYNTHETIC_ALIAS")||os.Getenv("OPENAI_ADMIN_KEY")!=""||
     os.Getenv("CONTROL_PLANE_API_KEY")!=os.Getenv("SYNTHETIC_RUNTIME"){refuse()}
   id:=os.Getenv("SYNTHETIC_TUNNEL")
   if os.Getenv("SYNTHETIC_FAULT")=="wrong-tunnel"{id="tunnel_ffffffffffffffffffffffffffffffff"}
   send(map[string]any{"alias":os.Getenv("SYNTHETIC_ALIAS"),"tunnel_id":id,"healthy":true,"ready":true,"process_running":true})
  case "stop":
   if os.Getenv("CONTROL_PLANE_API_KEY")!=""||os.Getenv("OPENAI_ADMIN_KEY")!=""||
     a[len(a)-1]!=os.Getenv("SYNTHETIC_ALIAS"){refuse()}
   send(map[string]any{"stopped":true})
  default:refuse()
  }
  return
 }
 if a[0]=="codex"{
  if os.Getenv("OPENAI_ADMIN_KEY")!=""||os.Getenv("CONTROL_PLANE_API_KEY")!=""{refuse()}
  if a[1]=="plugin"&&a[2]=="install"{send(map[string]any{"installed":true});return}
  if a[1]=="status"{send(map[string]any{"plugin_installed":true});return}
 }
 refuse()
}
