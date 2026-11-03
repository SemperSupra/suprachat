package main
import (
 "encoding/json"
 "flag"
 "fmt"
 "io"
 "os"
 "runtime"
 m "github.com/SemperSupra/suprachat/tools/oec-mirror"
)
func main() {
 input:=flag.String("input","","read a strictly typed synthetic metadata manifest")
 probe:=flag.Bool("runner-probe",false,"emit safe runner-local platform metadata")
 flag.Parse()
 if *probe {
  b,_:=json.MarshalIndent(map[string]string{"schema":"oec-runner-probe/v1","os":runtime.GOOS,"arch":runtime.GOARCH,"go":runtime.Version(),"evidence_class":"DIRECT_RUNNER"},"","  ")
  fmt.Println(string(b));return
 }
 if *input=="" {fmt.Fprintln(os.Stderr,"require -input or -runner-probe");os.Exit(2)}
 f,e:=os.Open(*input);if e!=nil{fmt.Fprintln(os.Stderr,e);os.Exit(1)}
 defer f.Close()
 b,e:=io.ReadAll(io.LimitReader(f,m.MaxInputBytes+1))
 if e!=nil{fmt.Fprintln(os.Stderr,e);os.Exit(1)}
 report,e:=m.Analyze(b);if e!=nil{fmt.Fprintln(os.Stderr,e);os.Exit(1)}
 out,e:=m.EncodeReport(report);if e!=nil{fmt.Fprintln(os.Stderr,e);os.Exit(1)}
 fmt.Println(string(out))
}
