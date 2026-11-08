#!/usr/bin/env python3
"""Disposable, model-free headless SupraChat stdio protocol qualification.

No session credentials, model calls, browser UI, background service, operator
grants, or Agent Dispatch task launch. This proves only observable JSON-RPC.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
OUTPUT=ROOT/"evidence"/"headless-stdio-model-free.json"

def main():
    dll=ROOT/"SupraChat.Automation"/"bin"/"Release"/"net10.0"/"SupraChat.Automation.dll"
    if not dll.is_file():
        raise RuntimeError("compiled headless automation DLL missing")
    frames=[
      {"jsonrpc":"2.0","id":"read-1","method":"session/ledger/read","params":{}},
      {"jsonrpc":"2.0","id":"cap-2","method":"capabilities/read","params":{}},
      {"jsonrpc":"2.0","id":"absent-3","method":"not-a-real-method","params":{}},
      {"jsonrpc":"2.0","id":"read-4","method":"session/ledger/read","params":{}},
    ]
    clean_env={k:v for k,v in os.environ.items()
               if not k.startswith(("OPENAI_API_KEY","SUPRACHAT_LEDGER_TOKEN",
                                  "SUPRACHAT_BUNDLE_PASSPHRASE","CODEX_API_KEY"))}
    proc=subprocess.Popen(["dotnet",str(dll),"stdio"],cwd=ROOT,
        stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
        text=True,env=clean_env)
    try:
        output,errors=proc.communicate("\n".join(json.dumps(x) for x in frames)+"\n",timeout=45)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.communicate()
        raise RuntimeError("stdio protocol timed out")
    responses=[]
    for line in output.splitlines():
        if line.strip():responses.append(json.loads(line))
    by_id={str(x.get("id")):x for x in responses}
    tests={
      "exit_0_after_closed_stdin":proc.returncode==0,
      "four_independent_responses":len(responses)==4 and len(by_id)==4,
      "protocol_request_ids_preserved":set(by_id)=={f["id"] for f in frames},
      "session_ledger_read_accepted":"result" in by_id.get("read-1",{}),
      "capabilities_read_accepted":"result" in by_id.get("cap-2",{}),
      "unsupported_method_denied":"error" in by_id.get("absent-3",{}) and
          "result" not in by_id.get("absent-3",{}),
      "subsequent_read_still_works":"result" in by_id.get("read-4",{}),
      "stdio_diagnostic_present":'stdio-ready' in errors,
    }
    report={
      "schema":"suprachat/headless-model-free-stdio-rdte/v1",
      "status":"PASS" if all(tests.values()) else "FAIL",
      "checks":tests,
      "model_or_chatgpt_product_invoked":False,
      "session_actor_admission_tested":False,
      "external_task_execution_tested":False,
      "provider_session_effect_tested":False,
      "native_chatgpt_task_api_tested":False,
      "upstream_output_sanitized":True,
    }
    OUTPUT.parent.mkdir(parents=True,exist_ok=True)
    OUTPUT.write_text(json.dumps(report,indent=2)+"\n",encoding="utf8")
    print(json.dumps(report,indent=2))
    return 0 if report["status"]=="PASS" else 1

if __name__=="__main__":
    try:sys.exit(main())
    except Exception as e:
        OUTPUT.parent.mkdir(parents=True,exist_ok=True)
        data={"status":"FAIL","reason":type(e).__name__+": "+str(e)[:250]}
        OUTPUT.write_text(json.dumps(data,indent=2)+"\n",encoding="utf8")
        print(json.dumps(data,indent=2))
        sys.exit(1)
