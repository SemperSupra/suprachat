package passport
import("encoding/json";"strings";"testing")
func fixture()(Receipt,Receipt,Receipt){
 a:=Receipt{
  "schema":"oec-inspector-public-receipt/v1","source":"@modelcontextprotocol/inspector@2.13.0",
  "target":"oec-observer-v02","transport":"loopback_http",
  "methods_qualified":[]string{"initialize","tools/list","tools/call"},
  "catalog_count_expected":7,"negative_invalid_bounds":"PASS","contains_credentials":false,
  "exposes_chatgpt_internals":false,"evidence_class":"DIRECT_INSPECTOR_SYNTHETIC_MCP",
 }
 b:=Receipt{
  "schema":"oec-chrome-devtools-witness/v1","upstream":"chrome-devtools-mcp@1.12.0",
  "evidence_class":"DIRECT_BROWSER_SYNTHETIC_FIXTURE","chrome_headless_isolated":true,
  "browser_snapshot":"PASS","synthetic_network_request":"PASS","synthetic_console_marker":"PASS",
  "performance_trace":"PASS","observed_tools":[]string{"new_page","list_pages","take_snapshot",
  "list_network_requests","list_console_messages","performance_start_trace","performance_stop_trace"},
  "upstream_tool_count":8,"raw_requests_exported":false,
  "session_cookies_exported":false,"real_chatgpt_browser":false,
 }
 c:=Receipt{
  "schema":"oec-otel-synthetic-receipt/v1","collector_submission":"HTTP_200",
  "trace_id":strings.Repeat("ab",16),"parent_child_span_count":2,"expected_negative_span":true,
  "sensitive_attribute_negatives":5,"no_prompts_or_credentials":true,
  "collector_debug_readback":"PASS","evidence_class":"DIRECT_COLLECTOR_SYNTHETIC_TRACES",
 }
 return a,b,c
}
func run(a,b,c Receipt)(Passport,error){
 x,_:=json.Marshal(a);y,_:=json.Marshal(b);z,_:=json.Marshal(c)
 return Build(x,y,z)
}
func TestActualReceiptShapes(t *testing.T){
 a,b,c:=fixture();p,e:=run(a,b,c)
 if e!=nil||p.Status!="QUALIFIED_SYNTHETIC_ONLY"||len(p.Sources)!=3||len(p.Unknown)!=5{t.Fatalf("unverified shape: %+v %v",p,e)}
 raw,_:=Encode(p)
 q,_:=Encode(p)
 if string(raw)!=string(q){t.Fatal("nondeterministic passport")}
}
func TestRejectUnauthorizedExpansion(t *testing.T){
 for _,tc:=range []struct{name string;change func(Receipt,Receipt,Receipt)}{
  {"claimed_internal",func(a,b,c Receipt){a["exposes_chatgpt_internals"]=true}},
  {"raw_browser",func(a,b,c Receipt){b["raw_requests_exported"]=true}},
  {"pretend_actual_chatgpt",func(a,b,c Receipt){b["real_chatgpt_browser"]=true}},
  {"unexpected_field",func(a,b,c Receipt){a["extra_tool"]="unreviewed"}},
  {"no_console",func(a,b,c Receipt){b["synthetic_console_marker"]="UNKNOWN"}},
  {"no_trace",func(a,b,c Receipt){c["collector_debug_readback"]="UNKNOWN"}},
  {"unexpected_trace",func(a,b,c Receipt){c["trace_id"]=strings.Repeat("cd",16)}},
  {"missing_method",func(a,b,c Receipt){a["methods_qualified"]=[]string{"initialize"}}},
  {"unknown_endpoint",func(a,b,c Receipt){c["extra"]="https://bad.invalid"}},
 }{
   t.Run(tc.name,func(t *testing.T){
     a,b,c:=fixture();tc.change(a,b,c);if _,e:=run(a,b,c);e==nil{t.Fatal("failed to reject unqualified evidence")}
   })
 }
}
