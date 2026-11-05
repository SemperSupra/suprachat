// Command tunnel-server exposes only fixed synthetic observation/action handlers.
// The listener is loopback-only. Public HTTPS ingress is provided by the
// separately checked Cloudflare Quick Tunnel in disposable GitHub Actions.
package main
import (
 "context"
 "encoding/hex"
 "fmt"
 "net"
 "net/http"
 "os"
 "os/signal"
 "syscall"
 "time"
 preflight "example.com/suprachat/oec-cloud-preflight"
)
func main(){
 key,err:=hex.DecodeString(os.Getenv("OEC_SECRET_HEX"))
 if err!=nil || len(key)!=32 || os.Getenv("OEC_RUN_ID")==""{
  fmt.Fprintln(os.Stderr,"SYNTHETIC_BINDING_REFUSED");os.Exit(2)
 }
 svc,err:=preflight.NewService(preflight.Config{
  RunID:os.Getenv("OEC_RUN_ID"),Generation:"synthetic-g1",Secret:key,Clock:time.Now})
 if err!=nil{fmt.Fprintln(os.Stderr,"SYNTHETIC_CONFIG_REFUSED");os.Exit(2)}
 listener,err:=net.Listen("tcp","127.0.0.1:18887")
 if err!=nil{fmt.Fprintln(os.Stderr,"LOOPBACK_BIND_FAILED");os.Exit(1)}
 server:=&http.Server{
  Handler:svc.Handler(),ReadHeaderTimeout:5*time.Second,
  ReadTimeout:10*time.Second,WriteTimeout:10*time.Second,
  IdleTimeout:10*time.Second,MaxHeaderBytes:4096}
 ctx,stop:=signal.NotifyContext(context.Background(),os.Interrupt,syscall.SIGTERM)
 defer stop()
 result:=make(chan error,1)
 go func(){result<-server.Serve(listener)}()
 fmt.Fprintln(os.Stderr,"SYNTHETIC_LOOPBACK_READY")
 select{
 case <-ctx.Done():
 case e:=<-result:
  if e!=nil && e!=http.ErrServerClosed{fmt.Fprintln(os.Stderr,"SYNTHETIC_SERVER_FAILURE");os.Exit(1)}
 }
 svc.Close()
 cleanup,cancel:=context.WithTimeout(context.Background(),4*time.Second);defer cancel()
 if err:=server.Shutdown(cleanup);err!=nil{fmt.Fprintln(os.Stderr,"SYNTHETIC_SERVER_SHUTDOWN_FAILED");os.Exit(1)}
 fmt.Fprintln(os.Stderr,"SYNTHETIC_LOOPBACK_STOPPED")
}
