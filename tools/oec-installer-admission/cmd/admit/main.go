package main
import (
 "bytes"
 "encoding/json"
 "flag"
 "fmt"
 "io"
 "os"
 admission "example.com/suprachat/oec-installer-admission"
)
func main(){
 input:=flag.String("request","","JSON request path (default stdin)")
 flag.Parse()
 var b []byte;var err error
 if *input!=""{b,err=os.ReadFile(*input)}else{b,err=io.ReadAll(io.LimitReader(os.Stdin,65537))}
 if err!=nil||len(b)>65536{fmt.Fprintln(os.Stderr,"ADMISSION_INPUT_REFUSED");os.Exit(2)}
 d:=json.NewDecoder(bytes.NewReader(b));d.DisallowUnknownFields()
 var req admission.Request
 if err=d.Decode(&req);err!=nil{fmt.Fprintln(os.Stderr,"ADMISSION_JSON_REFUSED");os.Exit(2)}
 if err=d.Decode(new(any));err!=io.EOF{fmt.Fprintln(os.Stderr,"ADMISSION_TRAILING_JSON_REFUSED");os.Exit(2)}
 p,err:=admission.For(req)
 if err!=nil{fmt.Fprintln(os.Stderr,"MODEL_A_OWNERSHIP_ADMISSION_REFUSED");os.Exit(2)}
 if err=json.NewEncoder(os.Stdout).Encode(p);err!=nil{os.Exit(2)}
}
