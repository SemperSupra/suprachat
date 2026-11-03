package tunnelexperiment
import (
 "encoding/json"
 "net/http"
 "net/http/httptest"
 "strings"
 "testing"
)
const testToken="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
func call(t *testing.T, h http.Handler, path, body, auth, origin string)(int,map[string]any){
 t.Helper();m:="POST";if path=="/healthz"{m="GET"}
 req:=httptest.NewRequest(m,path,strings.NewReader(body))
 req.Header.Set("Authorization",auth)
 req.Header.Set("Content-Type","application/json")
 if origin!=""{req.Header.Set("Origin",origin)}
 rec:=httptest.NewRecorder();h.ServeHTTP(rec,req)
 var obj map[string]any;_ =json.Unmarshal(rec.Body.Bytes(),&obj)
 return rec.Code,obj
}
func TestMCPInitializeAndTools(t *testing.T) {
 h,e:=Handler(testToken);if e!=nil{t.Fatal(e)}
 for _,q:=range []struct{body string; want int}{
  {`{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}`,200},
  {`{"jsonrpc":"2.0","id":2,"method":"tools/list"}`,200},
  {`{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lab.observe","arguments":{}}}`,200},
  {`{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"lab.digest","arguments":{"challenge":"123456789"}}}`,200},
 }{
  c,m:=call(t,h,"/mcp",q.body,"Bearer "+testToken,"");if c!=q.want||m["result"]==nil{t.Fatalf("contract %d %+v",c,m)}
 }
}
func TestAuthAndOrigins(t *testing.T){
 h,_:=Handler(testToken)
 body:=`{"jsonrpc":"2.0","id":1,"method":"tools/list"}`
 for _,x:=range []struct{auth,origin string;want int}{
  {"","",401},{"Bearer incorrect","",401},{"Bearer "+testToken,"https://example.invalid",403},{"Bearer "+testToken,"",200},
 }{
  c,_:=call(t,h,"/mcp",body,x.auth,x.origin);if c!=x.want{t.Fatalf("want %d got %d",x.want,c)}
 }
}
func TestUnknownToolAndArguments(t *testing.T){
 h,_:=Handler(testToken)
 for _,body:=range []string{
 `{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"shell.exec","arguments":{}}}`,
 `{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lab.observe","arguments":{"secret":"x"}}}`,
 `{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lab.digest","arguments":{"challenge":"a"}}}`,
 }{
  c,m:=call(t,h,"/mcp",body,"Bearer "+testToken,"")
  if c!=200||m["error"]==nil||m["result"]!=nil{t.Fatalf("should refuse %+v",m)}
 }
}
func TestBoundedBody(t *testing.T){
 h,_:=Handler(testToken);bad:=strings.Repeat("x",8193)
 c,_:=call(t,h,"/mcp",bad,"Bearer "+testToken,"")
 if c!=413{t.Fatalf("want 413 got %d",c)}
}
func TestNonstreamingOnly(t *testing.T){
 h,_:=Handler(testToken);c,m:=call(t,h,"/mcp",`{"jsonrpc":"2.0","id":4,"method":"ping"}`,"Bearer "+testToken,"")
 if c!=200||m["result"]==nil{t.Fatalf("ping failed %+v",m)}
}
