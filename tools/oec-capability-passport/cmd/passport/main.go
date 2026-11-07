package main
import(
 "flag"
 "fmt"
 "os"
 p "github.com/SemperSupra/suprachat/tools/oec-capability-passport"
)
func main(){
 inspector:=flag.String("inspector","","inspector receipt")
 chrome:=flag.String("chrome","","browser witness receipt")
 otel:=flag.String("otel","","collector receipt")
 output:=flag.String("out","","synthetic passport output")
 flag.Parse()
 if *inspector==""||*chrome==""||*otel==""||*output==""{fmt.Fprintln(os.Stderr,"all receipt and output paths are required");os.Exit(2)}
 a,e:=os.ReadFile(*inspector);if e!=nil{panic(e)}
 b,e:=os.ReadFile(*chrome);if e!=nil{panic(e)}
 c,e:=os.ReadFile(*otel);if e!=nil{panic(e)}
 pass,e:=p.Build(a,b,c);if e!=nil{fmt.Fprintln(os.Stderr,"REFUSED_UNQUALIFIED_SYNTHETIC_EVIDENCE");os.Exit(1)}
 out,e:=p.Encode(pass);if e!=nil{panic(e)}
 if e=os.WriteFile(*output,append(out,'\n'),0600);e!=nil{panic(e)}
 fmt.Println("QUALIFIED_OEC_CAPABILITY_PASSPORT sources=3 evidence_class=SYNTHETIC_ONLY unknowns=5")
}
