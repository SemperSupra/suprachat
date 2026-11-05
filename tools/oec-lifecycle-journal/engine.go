// Package lifecycle is a local, owner-scoped write-ahead transaction journal.
// This library has no OpenAI API, ChatGPT account or browser access.
package lifecycle

import (
 "context"
 "crypto/sha256"
 "encoding/hex"
 "encoding/json"
 "errors"
 "fmt"
 "os"
 "path/filepath"
 "reflect"
 "regexp"
)
const Schema = "suprachat-owned-install-journal/v1"
var (
 ErrReconcile=errors.New("manual reconcile required before retry")
 ErrDrift=errors.New("resource identity or independent proof drift")
 ErrScope=errors.New("invalid or foreign installation scope")
 ErrOperator=errors.New("operator detachment or authorization proof required")
 ErrAdapter=errors.New("adapter failed; intent retained for reconciliation")
 ErrLock=errors.New("another installer or stale lock owns installation")
)
var safe=regexp.MustCompile("^[a-z0-9][a-z0-9_.:-]{2,95}$")
var digest=regexp.MustCompile("^[a-f0-9]{64}$")
type Intent struct {
 ID string `json:"id"`
 Kind string `json:"kind"`
 Scope string `json:"scope"`
 Key string `json:"key"`
 Owner string `json:"owner"`
 Operation string `json:"operation"`
 Undo string `json:"undo"`
}
type Proof struct {
 Kind string `json:"kind"`
 Scope string `json:"scope"`
 Key string `json:"key"`
 Owner string `json:"owner"`
 RemoteID string `json:"remote_id,omitempty"`
 Fingerprint string `json:"fingerprint"`
}
type Entry struct {
 Intent Intent `json:"intent"`
 State string `json:"state"`
 Proof Proof `json:"proof"`
}
type Journal struct {
 Schema string `json:"schema"`
 InstallID string `json:"install_id"`
 Revision int `json:"revision"`
 Entries []Entry `json:"entries"`
}
type Adapter interface{
 Apply(context.Context,Intent)error
 Observe(context.Context,Intent)(Proof,bool,error)
 SafeToUndo(context.Context,Intent,Proof)(bool,error)
 Undo(context.Context,Intent,Proof)error
 ObserveAbsent(context.Context,Intent,Proof)(bool,error)
}
type Engine struct {
 root,path,lockPath string
 lock *os.File
 journal Journal
}
func (e *Engine)Snapshot()Journal{j:=e.journal;j.Entries=append([]Entry(nil),j.Entries...);return j}
func validIntent(i Intent,id string)bool{
 if !safe.MatchString(i.ID)||!safe.MatchString(i.Key)||!safe.MatchString(i.Scope)||i.Owner!=id{return false}
 allowed:=map[string][2]string{
  "platform_tunnel":{"create_owned_tunnel","delete_owned_tunnel"},
  "local_runtime":{"connect_owned_runtime","stop_owned_runtime"},
  "local_marketplace":{"register_owned_marketplace","remove_owned_marketplace"},
  "codex_plugin":{"install_owned_codex_plugin","uninstall_owned_codex_plugin"},
 }
 pair,ok:=allowed[i.Kind];return ok&&i.Operation==pair[0]&&i.Undo==pair[1]
}
func validProof(i Intent,p Proof)bool{
 if p.Kind!=i.Kind||p.Scope!=i.Scope||p.Key!=i.Key||p.Owner!=i.Owner||!digest.MatchString(p.Fingerprint){return false}
 if p.RemoteID!=""&&!safe.MatchString(p.RemoteID){return false}
 return i.Kind!="platform_tunnel"||p.RemoteID!=""
}
func validate(j Journal,id string)error{
 if j.Schema!=Schema||j.InstallID!=id||j.Revision<0||len(j.Entries)>64{return ErrScope}
 seen:=map[string]bool{}
 for _,r:=range j.Entries{
  if seen[r.Intent.ID]||!validIntent(r.Intent,id){return ErrScope};seen[r.Intent.ID]=true
  switch r.State{
  case "prepared":
   if r.Proof.Fingerprint!=""{return ErrScope}
  case "verified","undo_pending","removed":
   if !validProof(r.Intent,r.Proof){return ErrScope}
  default:return ErrScope
  }
 }
 return nil
}
func Open(root,id string)(*Engine,error){
 if !filepath.IsAbs(root)||!safe.MatchString(id){return nil,ErrScope}
 root=filepath.Clean(root)
 if err:=os.MkdirAll(root,0700);err!=nil{return nil,ErrScope}
 st,err:=os.Lstat(root);if err!=nil||st.Mode()&os.ModeSymlink!=0||!st.IsDir(){return nil,ErrScope}
 path:=filepath.Join(root,"journal-"+id+".json");lp:=path+".lock"
 lock,err:=os.OpenFile(lp,os.O_CREATE|os.O_EXCL|os.O_WRONLY,0600)
 if err!=nil{return nil,ErrLock}
 e:=&Engine{root:root,path:path,lockPath:lp,lock:lock,
  journal:Journal{Schema:Schema,InstallID:id,Entries:[]Entry{}}}
 if err=e.load();err!=nil{_ = e.Close();return nil,err}
 return e,nil
}
func (e *Engine)Close()error{
 if e.lock==nil{return nil}
 _=e.lock.Close();e.lock=nil;return os.Remove(e.lockPath)
}
func (e *Engine)load()error{
 st,err:=os.Lstat(e.path)
 if errors.Is(err,os.ErrNotExist){return nil}
 if err!=nil||st.Mode()&os.ModeSymlink!=0||!st.Mode().IsRegular()||st.Size()>262144{return ErrScope}
 b,err:=os.ReadFile(e.path);if err!=nil{return ErrScope}
 var j Journal
 if err=json.Unmarshal(b,&j);err!=nil{return ErrScope}
 if err=validate(j,e.journal.InstallID);err!=nil{return err}
 e.journal=j;return nil
}
func (e *Engine)save()error{
 e.journal.Revision++
 if err:=validate(e.journal,e.journal.InstallID);err!=nil{return err}
 b,err:=json.MarshalIndent(e.journal,"","  ")
 if err!=nil||len(b)>262144{return ErrScope}
 tmp,err:=os.CreateTemp(e.root,".owned-journal-*.tmp")
 if err!=nil{return ErrScope}
 defer os.Remove(tmp.Name())
 if err=tmp.Chmod(0600);err!=nil{_ = tmp.Close();return ErrScope}
 if _,err=tmp.Write(append(b,'\n'));err!=nil{_ = tmp.Close();return ErrScope}
 if err=tmp.Sync();err!=nil{_ = tmp.Close();return ErrScope}
 if err=tmp.Close();err!=nil{return ErrScope}
 if err=os.Rename(tmp.Name(),e.path);err!=nil{return ErrScope}
 return nil
}
func (e *Engine)index(id string)int{
 for n,x:=range e.journal.Entries{if x.Intent.ID==id{return n}}
 return -1
}
func (e *Engine)mark(n int,state string,p Proof)error{
 e.journal.Entries[n].State=state;e.journal.Entries[n].Proof=p;return e.save()
}
func (e *Engine)Install(ctx context.Context,intents []Intent,a Adapter)error{
 if e.lock==nil||len(intents)==0||len(intents)>64{return ErrScope}
 seen:=map[string]bool{}
 for _,i:=range intents{
  if !validIntent(i,e.journal.InstallID)||seen[i.ID]{return ErrScope}
  seen[i.ID]=true
  if n:=e.index(i.ID);n>=0&&e.journal.Entries[n].Intent!=i{return ErrDrift}
 }
 for _,i:=range intents{
  n:=e.index(i.ID)
  if n<0{
   e.journal.Entries=append(e.journal.Entries,Entry{Intent:i,State:"prepared"})
   if err:=e.save();err!=nil{return err}
   n=e.index(i.ID)
   if err:=a.Apply(ctx,i);err!=nil{return ErrAdapter}
  }
  rec:=e.journal.Entries[n]
  if rec.State=="removed"||rec.State=="undo_pending"{return ErrReconcile}
  p,exists,err:=a.Observe(ctx,i)
  if err!=nil{return ErrAdapter}
  if !exists||!validProof(i,p){return ErrReconcile}
  if rec.State=="verified"{
   if !reflect.DeepEqual(rec.Proof,p){return ErrDrift}
   continue
  }
  if rec.State!="prepared"{return ErrReconcile}
  if err=e.mark(n,"verified",p);err!=nil{return err}
 }
 return nil
}
func (e *Engine)Uninstall(ctx context.Context,a Adapter)error{
 if e.lock==nil{return ErrScope}
 for n:=len(e.journal.Entries)-1;n>=0;n--{
  rec:=e.journal.Entries[n]
  if rec.State=="removed"{continue}
  if rec.State=="undo_pending"{
   absent,err:=a.ObserveAbsent(ctx,rec.Intent,rec.Proof)
   if err!=nil{return ErrAdapter}
   if !absent{return ErrReconcile}
   if err=e.mark(n,"removed",rec.Proof);err!=nil{return err}
   continue
  }
  if rec.State!="verified"{return ErrReconcile}
  p,exists,err:=a.Observe(ctx,rec.Intent)
  if err!=nil{return ErrAdapter}
  if !exists||!reflect.DeepEqual(p,rec.Proof){return ErrDrift}
  allowed,err:=a.SafeToUndo(ctx,rec.Intent,rec.Proof)
  if err!=nil{return ErrAdapter}
  if !allowed{return ErrOperator}
  if err=e.mark(n,"undo_pending",rec.Proof);err!=nil{return err}
  if err=a.Undo(ctx,rec.Intent,rec.Proof);err!=nil{return ErrAdapter}
  absent,err:=a.ObserveAbsent(ctx,rec.Intent,rec.Proof)
  if err!=nil{return ErrAdapter}
  if !absent{return ErrReconcile}
  if err=e.mark(n,"removed",rec.Proof);err!=nil{return err}
 }
 return nil
}
func SyntheticFingerprint(s string)string{
 sum:=sha256.Sum256([]byte(s));return hex.EncodeToString(sum[:])
}
func (e *Engine)HumanSummary()string{
 counts:=map[string]int{}
 for _,r:=range e.journal.Entries{counts[r.State]++}
 return fmt.Sprintf("install=%s verified=%d prepared=%d undo_pending=%d removed=%d",
  e.journal.InstallID,counts["verified"],counts["prepared"],counts["undo_pending"],counts["removed"])
}
