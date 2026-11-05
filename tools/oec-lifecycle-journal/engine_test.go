package lifecycle
import (
 "context"
 "errors"
 "os"
 "path/filepath"
 "reflect"
 "strings"
 "testing"
)
type fake struct {
 live map[string]Proof
 applied,undone int
 failCreateAfterEffect,failUndoAfterEffect bool
 attached map[string]bool
 overridden map[string]Proof
}
func (f *fake)Apply(_ context.Context,i Intent)error{
 f.applied++;if f.live==nil{f.live=map[string]Proof{}}
 f.live[i.ID]=makeProof(i)
 if f.failCreateAfterEffect{return errors.New("PRIVATE_TOKEN_TEST_CANARY")}
 return nil
}
func (f *fake)Observe(_ context.Context,i Intent)(Proof,bool,error){
 if p,ok:=f.overridden[i.ID];ok{return p,true,nil}
 p,ok:=f.live[i.ID];return p,ok,nil
}
func (f *fake)SafeToUndo(_ context.Context,i Intent,p Proof)(bool,error){
 return !f.attached[i.ID],nil
}
func (f *fake)Undo(_ context.Context,i Intent,p Proof)error{
 f.undone++;delete(f.live,i.ID)
 if f.failUndoAfterEffect{return errors.New("PRIVATE_UNDO_TEST_CANARY")}
 return nil
}
func (f *fake)ObserveAbsent(_ context.Context,i Intent,p Proof)(bool,error){
 _,ok:=f.live[i.ID];return !ok,nil
}
func makeProof(i Intent)Proof{
 id:="";if i.Kind=="platform_tunnel"{id="tunnel_"+strings.ReplaceAll(i.Key,"-","_")}
 return Proof{Kind:i.Kind,Scope:i.Scope,Key:i.Key,Owner:i.Owner,RemoteID:id,
  Fingerprint:SyntheticFingerprint(i.Owner+"/"+i.Scope+"/"+i.Key)}
}
func intents(owner,scope,suffix string)[]Intent{return []Intent{
 {ID:"tunnel-"+suffix,Kind:"platform_tunnel",Scope:scope,Key:"tunnel-"+suffix,
 Owner:owner,Operation:"create_owned_tunnel",Undo:"delete_owned_tunnel"},
 {ID:"runtime-"+suffix,Kind:"local_runtime",Scope:scope,Key:"runtime-"+suffix,
 Owner:owner,Operation:"connect_owned_runtime",Undo:"stop_owned_runtime"},
}}
func open(t *testing.T,root,id string)*Engine{
 t.Helper();e,err:=Open(root,id);if err!=nil{t.Fatal(err)};return e
}
func TestInstallRepeatRestartUninstall(t *testing.T){
 ctx:=context.Background();root:=t.TempDir();id:="install-home"
 e:=open(t,root,id);f:=&fake{};i:=intents(id,"workspace-user","home")
 if err:=e.Install(ctx,i,f);err!=nil{t.Fatal(err)}
 if err:=e.Install(ctx,i,f);err!=nil||f.applied!=2{t.Fatal("duplicate creation")}
 original:=e.Snapshot();e.Close()
 e=open(t,root,id);defer e.Close()
 if !reflect.DeepEqual(original,e.Snapshot()){t.Fatal("disk journal differs")}
 if err:=e.Uninstall(ctx,f);err!=nil{t.Fatal(err)}
 if err:=e.Uninstall(ctx,f);err!=nil||f.undone!=2{t.Fatal("duplicate deletion")}
 for _,x:=range e.Snapshot().Entries{if x.State!="removed"{t.Fatal("missing removal tombstone")}}
 b,err:=os.ReadFile(filepath.Join(root,"journal-"+id+".json"))
 if err!=nil{t.Fatal(err)}
 if strings.Contains(string(b),"PRIVATE_TOKEN_TEST_CANARY"){t.Fatal("adapter secret captured")}
}
func TestCrashAfterCreateObservesInsteadOfReapplying(t *testing.T){
 root:=t.TempDir();id:="install-crash";i:=intents(id,"workspace-one","home")
 e:=open(t,root,id);f:=&fake{failCreateAfterEffect:true}
 if !errors.Is(e.Install(context.Background(),i,f),ErrAdapter){t.Fatal("creation error unreported")}
 if e.Snapshot().Entries[0].State!="prepared"{t.Fatal("missing write-ahead intent")}
 e.Close();f.failCreateAfterEffect=false
 e=open(t,root,id);defer e.Close()
 if err:=e.Install(context.Background(),i,f);err!=nil||f.applied!=2{t.Fatal("already-created tunnel replayed")}
}
func TestCrashWithUnobservableCreateStops(t *testing.T){
 root:=t.TempDir();id:="install-crash";i:=intents(id,"workspace-one","home")
 e:=open(t,root,id);f:=&fake{failCreateAfterEffect:true}
 _=e.Install(context.Background(),i,f);e.Close()
 f.live=map[string]Proof{};f.failCreateAfterEffect=false
 e=open(t,root,id);defer e.Close()
 if !errors.Is(e.Install(context.Background(),i,f),ErrReconcile)||f.applied!=1{t.Fatal("uncertain create replayed")}
}
func TestCrashAfterDeleteObservesInsteadOfRedeleting(t *testing.T){
 root:=t.TempDir();id:="install-delete";i:=intents(id,"workspace-one","home")
 e:=open(t,root,id);f:=&fake{}
 if err:=e.Install(context.Background(),i,f);err!=nil{t.Fatal(err)}
 f.failUndoAfterEffect=true
 if !errors.Is(e.Uninstall(context.Background(),f),ErrAdapter){t.Fatal("failed deletion unreported")}
 if e.Snapshot().Entries[1].State!="undo_pending"{t.Fatal("missing undo write-ahead")}
 e.Close();e=open(t,root,id);defer e.Close();f.failUndoAfterEffect=false
 if err:=e.Uninstall(context.Background(),f);err!=nil||f.undone!=2{t.Fatal("ambiguous delete replayed")}
}
func TestIndependentInstallsAndAttachedTunnelHold(t *testing.T){
 root:=t.TempDir();ctx:=context.Background()
 a:=open(t,root,"install-home");b:=open(t,root,"install-work");defer a.Close();defer b.Close()
 f:=&fake{attached:map[string]bool{"tunnel-home":true}}
 if err:=a.Install(ctx,intents("install-home","same-workspace","home"),f);err!=nil{t.Fatal(err)}
 if err:=b.Install(ctx,intents("install-work","same-workspace","work"),f);err!=nil{t.Fatal(err)}
 if err:=b.Uninstall(ctx,f);err!=nil{t.Fatal(err)}
 if !errors.Is(a.Uninstall(ctx,f),ErrOperator){t.Fatal("still-attached tunnel removed")}
 if f.undone!=2{t.Fatal("runtime removed before global ChatGPT attachment preflight")}
 if _,exists:=f.live["runtime-home"];!exists{t.Fatal("runtime stopped while ChatGPT connection attached")}
 if _,exists:=f.live["tunnel-home"];!exists{t.Fatal("home tunnel missing")}
 if _,exists:=f.live["tunnel-work"];exists{t.Fatal("work tunnel still live")}
}
func TestDriftForeignAndUnattendedChatGPTRefusal(t *testing.T){
 e:=open(t,t.TempDir(),"install-aaa");defer e.Close()
 ctx:=context.Background();f:=&fake{};i:=intents("install-aaa","scope-one","home")
 if err:=e.Install(ctx,i,f);err!=nil{t.Fatal(err)}
 wrong:=makeProof(i[0]);wrong.Fingerprint=SyntheticFingerprint("tampered")
 f.overridden=map[string]Proof{i[0].ID:wrong}
 if !errors.Is(e.Install(ctx,i,f),ErrDrift)||!errors.Is(e.Uninstall(ctx,f),ErrDrift){t.Fatal("resource drift waived")}
 f.overridden=nil
 if !errors.Is(e.Install(ctx,intents("foreign-owner","scope-one","home"),f),ErrScope){t.Fatal("foreign owner adopted")}
 if !errors.Is(e.Install(ctx,[]Intent{i[0],i[0]},f),ErrScope){t.Fatal("duplicate intent accepted")}
 chatgpt:=Intent{ID:"plugin-aaa",Kind:"chatgpt_plugin",Scope:"scope-one",Key:"key-aaa",Owner:"install-aaa",
  Operation:"operator_register_chatgpt_plugin",Undo:"operator_disconnect"}
 if !errors.Is(e.Install(ctx,[]Intent{chatgpt},f),ErrScope)||f.applied!=2{t.Fatal("unattended account action ran")}
}
func TestExclusiveLockAndTamperedJournal(t *testing.T){
 root:=t.TempDir();e:=open(t,root,"install-aaa")
 if _,err:=Open(root,"install-aaa");!errors.Is(err,ErrLock){t.Fatal("concurrent installer admitted")}
 e.Close()
 p:=filepath.Join(root,"journal-install-aaa.json")
 if err:=os.WriteFile(p,[]byte("{\"schema\":\"foreign\",\"install_id\":\"install-aaa\"}"),0600);err!=nil{t.Fatal(err)}
 if _,err:=Open(root,"install-aaa");!errors.Is(err,ErrScope){t.Fatal("corrupt authority accepted")}
}

func TestUninstallPreflightRefusesDriftWithoutPartialEffect(t *testing.T){
 root:=t.TempDir();id:="install-aaa";e:=open(t,root,id);defer e.Close()
 a:=intents(id,"workspace-one","alpha")
 f:=&fake{}
 if err:=e.Install(context.Background(),a,f);err!=nil{t.Fatal(err)}
 wrong:=makeProof(a[0]);wrong.Fingerprint=SyntheticFingerprint("changed")
 f.overridden=map[string]Proof{a[0].ID:wrong}
 if !errors.Is(e.Uninstall(context.Background(),f),ErrDrift){t.Fatal("drift not rejected")}
 if f.undone!=0{t.Fatal("destructive partial uninstall before detecting drift")}
 if _,ok:=f.live[a[1].ID];!ok{t.Fatal("unrelated runtime was removed")}
}
func TestUninstallPreflightRefusesPreparedStateWithoutPartialEffect(t *testing.T){
 root:=t.TempDir();id:="install-aaa";e:=open(t,root,id);defer e.Close()
 a:=intents(id,"workspace-one","alpha")
 f:=&fake{failCreateAfterEffect:true}
 if !errors.Is(e.Install(context.Background(),a,f),ErrAdapter){t.Fatal("expected interrupted create")}
 if !errors.Is(e.Uninstall(context.Background(),f),ErrReconcile){t.Fatal("uncertain intent allowed uninstall")}
 if f.undone!=0{t.Fatal("prepared action triggered deletion")}
}
