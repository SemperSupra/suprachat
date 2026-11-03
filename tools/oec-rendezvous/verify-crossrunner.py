#!/usr/bin/env python3
import argparse, hashlib, importlib.util, json, os, pathlib, secrets, sys, time
origin=pathlib.Path(__file__).resolve().parents[1]/"ephemeral-tunnel-lab"/"verify_remote.py"
spec=importlib.util.spec_from_file_location("baseverifier",origin)
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
stage="input"
try:
 p=argparse.ArgumentParser()
 p.add_argument("--url",required=True)
 p.add_argument("--server-nonce",required=True)
 p.add_argument("--receipt",required=True)
 a=p.parse_args()
 token=os.environ["OEC_LAB_TOKEN"]
 assert len(token)>=48 and len(a.server_nonce)==24
 assert a.url.startswith("https://") and a.url.endswith(".trycloudflare.com")
 stage="initialize"
 for i in range(24):
  try:
   code,init=m.send(a.url,token,"initialize",1)
   if code==200 and init.get("result",{}).get("protocolVersion")=="2025-06-18":break
  except (OSError,ValueError):pass
  time.sleep(2)
 else:raise ValueError("not initialized")
 stage="tools"
 code,lst=m.send(a.url,token,"tools/list",2)
 assert code==200 and set(x["name"] for x in lst["result"]["tools"])=={"lab.observe","lab.digest"}
 stage="nonce"
 code,obs=m.send(a.url,token,"tools/call",3,{"name":"lab.observe","arguments":{}})
 info=obs["result"]["structuredContent"]
 assert code==200 and info["nonce"]==a.server_nonce and info["evidence_class"]=="DIRECT_RUNNER"
 stage="digest"
 challenge=secrets.token_hex(16)
 code,reply=m.send(a.url,token,"tools/call",4,{"name":"lab.digest","arguments":{"challenge":challenge}})
 detail=reply["result"]["structuredContent"]
 assert code==200 and detail["sha256"]==hashlib.sha256(challenge.encode()).hexdigest() and detail["nonce"]==a.server_nonce
 stage="authorization"
 m.negatives(a.url,token)
 receipt={"schema":"oec-ephemeral-tunnel-receipt/v1","evidence_class":"DIRECT_PUBLIC_HTTPS_SYNTHETIC_FIXTURE","runner_os":"linux","transport":"quick_tunnel_https_json_no_sse","mcp_initialization":"PASS","tool_discovery":"PASS","live_process_nonce_match":"PASS","challenge_digest_roundtrip":"PASS","unauthorized_401":"PASS","origin_403":"PASS","unknown_tool_refusal":"PASS","secrets_or_endpoints_exported":False,"chatgpt_internal_visibility":False,"independent_runner":True}
 with open(a.receipt,"w") as f:json.dump(receipt,f,sort_keys=True)
 print("QUALIFIED_INDEPENDENT_RUNNER_REMOTE_MCP")
except Exception:
 print("CROSS_RUNNER_FIRST_FAILURE stage="+stage,file=sys.stderr)
 sys.exit(1)
