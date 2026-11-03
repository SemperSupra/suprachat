package main
import (
 "flag"
 "log"
 "os"
 obs "github.com/SemperSupra/suprachat/tools/oec-observer-v02"
)
func main(){
 address:=flag.String("listen","127.0.0.1:18763","fixed loopback fixture")
 flag.Parse()
 if *address!="127.0.0.1:18763"{log.Fatal("loopback only")}
 handler,err:=obs.New(os.Getenv("OEC_OBSERVER_TOKEN"));if err!=nil{log.Fatal("invalid observer token")}
 log.Println("OEC_OBSERVER_READY synthetic_fixture_only")
 log.Fatal(obs.Listen(*address,handler).ListenAndServe())
}
