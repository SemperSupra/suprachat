// Package audit is a bounded read-only census of public GitHub pull request
// source blobs. It NEVER closes a PR, merges, deletes a branch or changes DLE.
package audit
import(
 "context"
 "encoding/json"
 "errors"
 "fmt"
 "io"
 "net/http"
 "regexp"
 "sort"
 "strings"
 "time"
)
const Schema="suprachat-public-pr-hygiene-census/v1"
var repoRE=regexp.MustCompile("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$")
var shaRE=regexp.MustCompile("^[a-f0-9]{40}$")
type PR struct{
 Number int `json:"number"`
 Title string `json:"title"`
 Head struct{SHA string `json:"sha"`} `json:"head"`
}
type File struct{
 Filename string `json:"filename"`
 Status string `json:"status"`
 SHA string `json:"sha"`
}
type treeResult struct{
 Truncated bool `json:"truncated"`
 Tree []struct{Path string `json:"path"`;Type string `json:"type"`;SHA string `json:"sha"`} `json:"tree"`
}
type Outcome struct{
 Number int `json:"number"`
 Title string `json:"title"`
 HeadSHA string `json:"head_sha"`
 Classification string `json:"classification"`
 ExactPaths int `json:"exact_paths"`
 UniquePaths int `json:"unique_paths"`
 DivergentPaths int `json:"divergent_paths"`
 Note string `json:"note"`
}
type Report struct{
 Schema string `json:"schema"`
 Repository string `json:"repository"`
 MainSHA string `json:"main_sha"`
 ReadOnly bool `json:"read_only"`
 ActionsApplied bool `json:"actions_applied"`
 PRs []Outcome `json:"prs"`
 Counts map[string]int `json:"counts"`
}
type Client struct{
 Root string
 Token string
 HTTP *http.Client
}
func (c Client)get(ctx context.Context,path string,v any)error{
 base:=strings.TrimSuffix(c.Root,"/")
 if !strings.HasPrefix(base,"https://")&&!strings.HasPrefix(base,"http://127.0.0.1:"){
  return errors.New("untrusted API origin")
 }
 if c.HTTP==nil{c.HTTP=&http.Client{Timeout:15*time.Second}}
 req,err:=http.NewRequestWithContext(ctx,http.MethodGet,base+path,nil)
 if err!=nil{return errors.New("request creation failed")}
 req.Header.Set("Accept","application/vnd.github+json")
 if c.Token!=""{req.Header.Set("Authorization","Bearer "+c.Token)}
 resp,err:=c.HTTP.Do(req);if err!=nil{return errors.New("GitHub read failed")}
 defer resp.Body.Close()
 if resp.StatusCode!=200{return fmt.Errorf("GitHub read rejected with HTTP %d",resp.StatusCode)}
 body,err:=io.ReadAll(io.LimitReader(resp.Body,2097153))
 if err!=nil||len(body)>2097152{return errors.New("GitHub response size exceeded")}
 if err=json.Unmarshal(body,v);err!=nil{return errors.New("GitHub JSON shape invalid")}
 return nil
}
// Collect reads a bounded 100-PR/100-file window. It refuses to label omitted
// pages as fully reconciled. Candidate classification is a REVIEW PROMPT,
// never proof of behavioral equivalence and never automated close authority.
func (c Client)Collect(ctx context.Context,repo,mainSHA string)(Report,error){
 if !repoRE.MatchString(repo)||!shaRE.MatchString(mainSHA){return Report{},errors.New("invalid repository or exact main SHA")}
 result:=Report{Schema:Schema,Repository:repo,MainSHA:mainSHA,ReadOnly:true,
  ActionsApplied:false,PRs:[]Outcome{},Counts:map[string]int{}}
 var tree treeResult
 if err:=c.get(ctx,"/repos/"+repo+"/git/trees/"+mainSHA+"?recursive=1",&tree);err!=nil{return Report{},err}
 if tree.Truncated||len(tree.Tree)>100000{return Report{},errors.New("main source tree incomplete")}
 main:=map[string]string{}
 for _,x:=range tree.Tree{
  if x.Type=="blob"{main[x.Path]=x.SHA}
 }
 var prs []PR
 if err:=c.get(ctx,"/repos/"+repo+"/pulls?state=open&per_page=100",&prs);err!=nil{return Report{},err}
 if len(prs)>=100{return Report{},errors.New("open PR pagination needed; refuse incomplete census")}
 sort.Slice(prs,func(i,j int)bool{return prs[i].Number<prs[j].Number})
 for _,p:=range prs{
  if p.Number<=0||!shaRE.MatchString(p.Head.SHA){return Report{},errors.New("invalid PR identity")}
  outcome:=Outcome{Number:p.Number,Title:p.Title,HeadSHA:p.Head.SHA}
  var files []File
  if err:=c.get(ctx,fmt.Sprintf("/repos/%s/pulls/%d/files?per_page=100",repo,p.Number),&files);err!=nil{return Report{},err}
  if len(files)>=100{
   outcome.Classification="MANUAL_LARGE_DIFF_REVIEW"
   outcome.Note="Changed files reach API page cap; no close/merge authority"
   result.PRs=append(result.PRs,outcome);result.Counts[outcome.Classification]++;continue
  }
  if len(files)==0{
   outcome.Classification="MANUAL_EMPTY_DIFF_REVIEW"
   outcome.Note="No changed paths returned; investigate branch history, do not auto-close"
   result.PRs=append(result.PRs,outcome);result.Counts[outcome.Classification]++;continue
  }
  for _,f:=range files{
   if f.Filename==""{return Report{},errors.New("invalid changed-file path")}
   source,exists:=main[f.Filename]
   switch {
   case f.Status=="removed"&&!exists:outcome.ExactPaths++
   case f.Status=="renamed":outcome.DivergentPaths++
   case !exists:outcome.UniquePaths++
   case f.Status!="removed"&&shaRE.MatchString(f.SHA)&&source==f.SHA:outcome.ExactPaths++
   default:outcome.DivergentPaths++
   }
  }
  switch {
  case outcome.UniquePaths>0:outcome.Classification="UNIQUE_SOURCE_PRESERVE"
  case outcome.DivergentPaths>0:outcome.Classification="DIVERGENT_SOURCE_REVIEW"
  default:outcome.Classification="EXACT_BLOBS_MANUAL_SUPERSESSION_REVIEW"
  }
  outcome.Note="Read-only candidate only. Inspect DLE ownership/leases, behavior and CI before any close or merge"
  result.PRs=append(result.PRs,outcome);result.Counts[outcome.Classification]++
 }
 return result,nil
}
