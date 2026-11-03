package passport

import (
 "bytes"
 "crypto/sha256"
 "encoding/hex"
 "encoding/json"
 "errors"
 "io"
 "sort"
 "strings"
)

type Receipt map[string]any
type Source struct {
 Workcell string `json:"workcell"`
 RunID string `json:"run_id"`
 Head string `json:"head_sha"`
 Digest string `json:"receipt_sha256"`
 Class string `json:"evidence_class"`
}
type Passport struct {
 Schema string `json:"schema"`
 Status string `json:"status"`
 Subject string `json:"subject"`
 Sources []Source `json:"sources"`
 Qualified []string `json:"qualified"`
 Unknown []string `json:"unknown"`
}
func parse(raw []byte,keys []string)(Receipt,error){
 if len(raw)==0||len(raw)>16384||bytes.Contains(raw,[]byte("://")){return nil,errors.New("unexpected payload")}
 var m Receipt
 d:=json.NewDecoder(bytes.NewReader(raw))
 if e:=d.Decode(&m);e!=nil{return nil,e}
 var extra any
 if d.Decode(&extra)!=io.EOF{return nil,errors.New("trailing data")}
 allowed:=make(map[string]bool)
 for _,k:=range keys{allowed[k]=true}
 if len(m)!=len(allowed){return nil,errors.New("receipt field drift")}
 for k:=range m {if !allowed[k]{return nil,errors.New("unrecognized field")}}
 return m,nil
}
func exact(m Receipt,key string,value any)bool{return m[key]==value}
func sha(raw []byte)string{v:=sha256.Sum256(raw);return hex.EncodeToString(v[:])}
func arrayStrings(m Receipt,key string)([]string,error){
 a,ok:=m[key].([]any);if !ok{return nil,errors.New("array missing")}
 out:=make([]string,0,len(a));for _,v:=range a{s,ok:=v.(string);if !ok{return nil,errors.New("array contains other type")};out=append(out,s)}
 return out,nil
}
func setContains(haystack []string,need []string)bool{
 known:=map[string]bool{};for _,v:=range haystack{if known[v]{return false};known[v]=true}
 for _,v:=range need{if !known[v]{return false}};return true
}
func Build(inspector,chrome,otel []byte)(Passport,error){
 bad:=func()(Passport,error){return Passport{},errors.New("evidence schema, claim or privacy mismatch")}
 a,err:=parse(inspector,[]string{"schema","source","target","transport","methods_qualified","catalog_count_expected","negative_invalid_bounds","contains_credentials","exposes_chatgpt_internals","evidence_class"})
 if err!=nil{return bad()}
 if !exact(a,"schema","oec-inspector-public-receipt/v1")||!exact(a,"source","@modelcontextprotocol/inspector@2.13.0")||!exact(a,"target","oec-observer-v02")||
 !exact(a,"transport","loopback_http")||!exact(a,"catalog_count_expected",float64(7))||!exact(a,"negative_invalid_bounds","PASS")||
 !exact(a,"contains_credentials",false)||!exact(a,"exposes_chatgpt_internals",false)||!exact(a,"evidence_class","DIRECT_INSPECTOR_SYNTHETIC_MCP"){return bad()}
 methods,e:=arrayStrings(a,"methods_qualified")
 if e!=nil||len(methods)!=3||!setContains(methods,[]string{"initialize","tools/list","tools/call"}){return bad()}
 b,err:=parse(chrome,[]string{"schema","upstream","evidence_class","chrome_headless_isolated","browser_snapshot","synthetic_network_request","synthetic_console_marker","performance_trace","observed_tools","upstream_tool_count","raw_requests_exported","session_cookies_exported","real_chatgpt_browser"})
 if err!=nil{return bad()}
 if !exact(b,"schema","oec-chrome-devtools-witness/v1")||!exact(b,"upstream","chrome-devtools-mcp@1.12.0")||
 !exact(b,"evidence_class","DIRECT_BROWSER_SYNTHETIC_FIXTURE")||!exact(b,"chrome_headless_isolated",true)||
 !exact(b,"browser_snapshot","PASS")||!exact(b,"synthetic_network_request","PASS")||
 !exact(b,"synthetic_console_marker","PASS")||!exact(b,"performance_trace","PASS")||
 !exact(b,"raw_requests_exported",false)||!exact(b,"session_cookies_exported",false)||
 !exact(b,"real_chatgpt_browser",false){return bad()}
 observed,e:=arrayStrings(b,"observed_tools")
 if e!=nil||!setContains(observed,[]string{"new_page","list_pages","take_snapshot","list_network_requests","list_console_messages","performance_start_trace","performance_stop_trace"}){return bad()}
 c,err:=parse(otel,[]string{"schema","collector_submission","trace_id","parent_child_span_count","expected_negative_span","sensitive_attribute_negatives","no_prompts_or_credentials","collector_debug_readback","evidence_class"})
 if err!=nil{return bad()}
 if !exact(c,"schema","oec-otel-synthetic-receipt/v1")||!exact(c,"collector_submission","HTTP_200")||
 !exact(c,"trace_id",strings.Repeat("ab",16))||!exact(c,"parent_child_span_count",float64(2))||
 !exact(c,"expected_negative_span",true)||!exact(c,"sensitive_attribute_negatives",float64(5))||
 !exact(c,"no_prompts_or_credentials",true)||!exact(c,"collector_debug_readback","PASS")||
 !exact(c,"evidence_class","DIRECT_COLLECTOR_SYNTHETIC_TRACES"){return bad()}
 sources:=[]Source{
  {"inspector","41661531502","4791b5910394bc07cf4628eb1d3abce512d2190b",sha(inspector),"DIRECT_INSPECTOR_SYNTHETIC_MCP"},
  {"chrome","41662816944","83301fec49a004ef9ddbe9d8580629eab67fa074",sha(chrome),"DIRECT_BROWSER_SYNTHETIC_FIXTURE"},
  {"otel","41661990184","144be27740c09e84d8693bcd8e2eab3b6d24a637",sha(otel),"DIRECT_COLLECTOR_SYNTHETIC_TRACES"},
 }
 sort.Slice(sources,func(i,j int)bool{return sources[i].Workcell<sources[j].Workcell})
 return Passport{"oec-observatory-capability-passport/v1","QUALIFIED_SYNTHETIC_ONLY","ephemeral-agent-observatory",sources,
 []string{"MCP schema/call and invalid-input behavior","synthetic browser accessibility/network/console/performance","synthetic trace ingestion and privacy negatives"},
 []string{"private ChatGPT internals","authenticated user browser","actual model/tool execution","external provider attestation","cross-service causality"}},nil
}
func Encode(p Passport)([]byte,error){return json.MarshalIndent(p,"","  ")}
