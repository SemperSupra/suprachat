// Read-only GitHub PR hygiene census. The only allowed GitHub HTTP method is GET.
package main
import(
 "context"
 "encoding/json"
 "flag"
 "fmt"
 "net/http"
 "os"
 "time"
 audit "example.com/suprachat/pr-hygiene-audit"
)
func main(){
 repo:=flag.String("repo",os.Getenv("GITHUB_REPOSITORY"),"public repository owner/name")
 mainSHA:=flag.String("main-sha","","exact public main commit SHA (no implicit ref)")
 flag.Parse()
 ctx,cancel:=context.WithTimeout(context.Background(),3*time.Minute);defer cancel()
 reader:=audit.Client{Root:"https://api.github.com",Token:os.Getenv("GITHUB_TOKEN"),
  HTTP:&http.Client{Timeout:15*time.Second}}
 report,err:=reader.Collect(ctx,*repo,*mainSHA)
 if err!=nil{fmt.Fprintln(os.Stderr,"READ_ONLY_CENSUS_REFUSED");os.Exit(2)}
 if err=json.NewEncoder(os.Stdout).Encode(report);err!=nil{os.Exit(2)}
}
