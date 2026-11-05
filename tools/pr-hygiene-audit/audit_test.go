package audit
import(
 "context"
 "encoding/json"
 "fmt"
 "net/http"
 "net/http/httptest"
 "reflect"
 "strings"
 "testing"
)
const mainSHA="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
const exactSHA="eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
const driftSHA="dddddddddddddddddddddddddddddddddddddddd"
const changedSHA="ffffffffffffffffffffffffffffffffffffffff"
func service(t *testing.T,variant string)*httptest.Server{
 t.Helper()
 return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter,r *http.Request){
  if r.Method!="GET"{t.Error("mutation attempted");http.Error(w,"no",405);return}
  if r.Header.Get("Authorization")!="Bearer SYNTHETIC_TOKEN_CANARY"{http.Error(w,"no",403);return}
  w.Header().Set("Content-Type","application/json")
  switch {
  case strings.Contains(r.URL.Path,"/git/trees/"):
   if variant=="truncated"{fmt.Fprint(w,`{"truncated":true,"tree":[]}`);return}
   fmt.Fprintf(w,`{"truncated":false,"tree":[{"path":"file/exact.go","sha":"%s","type":"blob"},{"path":"file/drift.go","sha":"%s","type":"blob"}]}`,exactSHA,driftSHA)
  case strings.HasSuffix(r.URL.Path,"/pulls"):
   if variant=="many"{
    pulls:=[]map[string]any{}
    for i:=1;i<=100;i++{pulls=append(pulls,map[string]any{"number":i,"head":map[string]any{"sha":mainSHA}})}
    _=json.NewEncoder(w).Encode(pulls);return
   }
   fmt.Fprintf(w,`[{"number":13,"title":"Unique source","head":{"sha":"%s"}},{"number":11,"title":"Exact code","head":{"sha":"%s"}},{"number":12,"title":"Divergent tests","head":{"sha":"%s"}},{"number":14,"title":"Already removed","head":{"sha":"%s"}}]`,mainSHA,mainSHA,mainSHA,mainSHA)
  case strings.Contains(r.URL.Path,"/pulls/11/files"):
   fmt.Fprintf(w,`[{"filename":"file/exact.go","sha":"%s","status":"modified"}]`,exactSHA)
  case strings.Contains(r.URL.Path,"/pulls/12/files"):
   fmt.Fprintf(w,`[{"filename":"file/drift.go","sha":"%s","status":"modified"}]`,changedSHA)
  case strings.Contains(r.URL.Path,"/pulls/13/files"):
   fmt.Fprintf(w,`[{"filename":"file/novel.go","sha":"%s","status":"added"}]`,changedSHA)
  case strings.Contains(r.URL.Path,"/pulls/14/files"):
   fmt.Fprintf(w,`[{"filename":"file/removed.go","sha":"%s","status":"removed"}]`,changedSHA)
  default:http.Error(w,"not found",404)
  }
 }))
}
func TestSourceClassifierDeterministicAndNoMutations(t *testing.T){
 srv:=service(t,"");defer srv.Close()
 c:=Client{Root:srv.URL,Token:"SYNTHETIC_TOKEN_CANARY",HTTP:srv.Client()}
 first,e:=c.Collect(context.Background(),"SemperSupra/suprachat",mainSHA)
 if e!=nil{t.Fatal(e)}
 second,e:=c.Collect(context.Background(),"SemperSupra/suprachat",mainSHA)
 if e!=nil||!reflect.DeepEqual(first,second){t.Fatal("nondeterministic census")}
 if !first.ReadOnly||first.ActionsApplied||len(first.PRs)!=4{t.Fatal("misrepresented mutation or missing PR")}
 expected:=[]string{
 "EXACT_BLOBS_MANUAL_SUPERSESSION_REVIEW","DIVERGENT_SOURCE_REVIEW",
 "UNIQUE_SOURCE_PRESERVE","EXACT_BLOBS_MANUAL_SUPERSESSION_REVIEW"}
 for i,p:=range first.PRs{
  if p.Classification!=expected[i]||p.Number!=11+i{t.Fatalf("bad candidate %d: %+v",i,p)}
 }
 raw,_:=json.Marshal(first)
 if strings.Contains(string(raw),"SYNTHETIC_TOKEN_CANARY"){t.Fatal("leaked GitHub credential in report")}
}
func TestFailClosedTruncatedCensusAndPagination(t *testing.T){
 for _,mode:=range []string{"truncated","many"}{
  srv:=service(t,mode)
  c:=Client{Root:srv.URL,Token:"SYNTHETIC_TOKEN_CANARY",HTTP:srv.Client()}
  if _,e:=c.Collect(context.Background(),"SemperSupra/suprachat",mainSHA);e==nil{t.Fatal("accepted unbounded or incomplete data")}
  srv.Close()
 }
}
func TestRejectUnsafeRepoMainAndOrigin(t *testing.T){
 srv:=service(t,"");defer srv.Close()
 c:=Client{Root:srv.URL,Token:"SYNTHETIC_TOKEN_CANARY",HTTP:srv.Client()}
 for _,repo:=range []string{"../../private","other/../private","", "bad name/repo"}{
  if _,e:=c.Collect(context.Background(),repo,mainSHA);e==nil{t.Fatal("malicious repo accepted")}
 }
 if _,e:=c.Collect(context.Background(),"SemperSupra/suprachat","main");e==nil{t.Fatal("mutable main ref accepted")}
 if _,e:=(Client{Root:"http://untrusted.example",Token:"SYNTHETIC_TOKEN_CANARY"}).
    Collect(context.Background(),"SemperSupra/suprachat",mainSHA);e==nil{
  t.Fatal("untrusted host accepted")
 }
}
func TestTokenRequiredNeverEchoed(t *testing.T){
 srv:=service(t,"");defer srv.Close()
 c:=Client{Root:srv.URL,Token:"INVALID_SECRET",HTTP:srv.Client()}
 if _,e:=c.Collect(context.Background(),"SemperSupra/suprachat",mainSHA);e==nil||
  strings.Contains(e.Error(),"INVALID_SECRET"){t.Fatal("denial missing or leaked token")}
}
