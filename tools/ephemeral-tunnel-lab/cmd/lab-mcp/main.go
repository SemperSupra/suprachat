package main
import (
 "flag"
 "fmt"
 "log"
 "os"
 "github.com/SemperSupra/suprachat/tools/ephemeral-tunnel-lab"
)
func main(){
 addr:=flag.String("listen","127.0.0.1:18761","loopback-only local fixture port")
 flag.Parse()
 if *addr!="127.0.0.1:18761" {log.Fatal("fixed loopback fixture only")}
 token:=os.Getenv("OEC_LAB_TOKEN")
 h,err:=tunnelexperiment.Handler(token);if err!=nil{log.Fatal(err)}
 fmt.Fprintln(os.Stderr,"OEC_LAB_MCP_READY scope=synthetic_only (no token, URL or raw payload logged)")
 log.Fatal(tunnelexperiment.HTTPServer(*addr,h).ListenAndServe())
}
