package main
import (
 "encoding/json"
 "flag"
 "fmt"
 "os"
 provision "example.com/suprachat/provision"
)
func main(){
 path:=flag.String("request","","Path to a bounded JSON request")
 flag.Parse()
 if *path==""{fmt.Fprintln(os.Stderr,"MISSING_REQUEST");os.Exit(2)}
 b,e:=os.ReadFile(*path)
 if e!=nil||len(b)>65536{fmt.Fprintln(os.Stderr,"REQUEST_REFUSED");os.Exit(2)}
 var req provision.Request
 if e=json.Unmarshal(b,&req);e!=nil{fmt.Fprintln(os.Stderr,"INVALID_JSON");os.Exit(2)}
 p,e:=provision.Build(req)
 if e!=nil{fmt.Fprintln(os.Stderr,"INVALID_RESOURCE_SCOPE");os.Exit(2)}
 if e=json.NewEncoder(os.Stdout).Encode(p);e!=nil{os.Exit(2)}
}
