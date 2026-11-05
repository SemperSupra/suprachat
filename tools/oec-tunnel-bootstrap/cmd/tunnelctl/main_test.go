package main
import (
 "strings"
 "testing"
 bootstrap "example.com/suprachat/oec-tunnel-bootstrap"
)
func TestChildEnvironmentSecretBoundaries(t *testing.T){
 t.Setenv("CONTROL_PLANE_API_KEY","SYNTHETIC_RUNTIME_KEY_CANARY")
 t.Setenv("OPENAI_ADMIN_KEY","SYNTHETIC_ADMIN_KEY_CANARY")
 parent:=[]string{
  "HOME=/synthetic/home","PATH=/synthetic/bin",
  "OPENAI_API_KEY=EXTRA_SECRET","OPENAI_ADMIN_KEY=BAD_ADMIN",
  "CONTROL_PLANE_API_KEY=BAD_RUNTIME","GH_TOKEN=BAD_GH","GITHUB_TOKEN=BAD_GITHUB",
  "CONTROL_PLANE_TUNNEL_ID=BAD_ID","MCP_SERVER_URL=http://wrong.invalid/mcp",
 }
 cases:=[]struct{
  name string;cmd bootstrap.Command;runtime,admin bool
 }{
  {"runtime_connect",bootstrap.Command{Args:[]string{"runtimes","--json","connect"}},true,false},
  {"runtime_status",bootstrap.Command{Args:[]string{"runtimes","--json","status"}},true,false},
  {"admin_create",bootstrap.Command{Args:[]string{"runtimes","--json","connect"},Admin:true},true,true},
  {"codex_install",bootstrap.Command{Args:[]string{"codex","plugin","install"}},false,false},
  {"codex_status",bootstrap.Command{Args:[]string{"codex","status","--json"}},false,false},
  {"local_stop",bootstrap.Command{Args:[]string{"runtimes","--json","stop"}},false,false},
 }
 for _,tc:=range cases{t.Run(tc.name,func(t *testing.T){
  env:=sanitizedChildEnv(parent,tc.cmd)
  joined:=strings.Join(env,"\n")
  for _,secret:=range []string{"EXTRA_SECRET","BAD_ADMIN","BAD_RUNTIME","BAD_GH","BAD_GITHUB","BAD_ID","wrong.invalid"}{
   if strings.Contains(joined,secret){t.Fatalf("inherited unsafe value: %s",secret)}
  }
  runtime:=strings.Contains(joined,"CONTROL_PLANE_API_KEY=SYNTHETIC_RUNTIME_KEY_CANARY")
  admin:=strings.Contains(joined,"OPENAI_ADMIN_KEY=SYNTHETIC_ADMIN_KEY_CANARY")
  if runtime!=tc.runtime||admin!=tc.admin{t.Fatalf("credential split wrong runtime=%t admin=%t",runtime,admin)}
  if !strings.Contains(joined,"HOME=/synthetic/home") {t.Fatal("benign env stripped")}
 })}
}
