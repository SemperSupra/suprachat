#!/usr/bin/env python3
"""R49 read-only cross-head Windows UIA failure-lineage classifier.

Only consumes bounded, pre-redacted exact GitHub Actions observations. It
does not download logs, access a local desktop, waive CI, or diagnose a root
cause from a common terminal symptom.
"""
from __future__ import annotations
import argparse
import json
import re
from pathlib import Path

SCHEMA="suprachat-uia-failure-lineage/v1"
SIGNATURE="UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS"
SHA=re.compile(r"^[0-9a-f]{40}$")
MAX=24
STAGE="installed-winapp-native-uia"
OS="windows-2025"

def critic(doc):
    if not isinstance(doc,dict) or set(doc)!={"schema","repository","observations"}:
        raise ValueError("invalid input fields")
    if doc["schema"]!=SCHEMA or doc["repository"]!="SemperSupra/suprachat":
        raise ValueError("wrong evidence authority")
    obs=doc["observations"]
    if not isinstance(obs,list) or not 1<=len(obs)<=MAX:
        raise ValueError("invalid observation count")
    seen=set(); jobs=set(); heads=set(); verified=[]
    for x in obs:
        if not isinstance(x,dict) or set(x)!={
             "run_id","job_id","head_sha","runner","stage","terminal_marker",
             "host_exit_code","conclusion","evidence_kind"}:
            raise ValueError("unknown or sensitive evidence field")
        if (type(x["run_id"]) is not int or x["run_id"]<=0 or
            type(x["job_id"]) is not int or x["job_id"]<=0 or
            x["run_id"] in seen or x["job_id"] in jobs):
            raise ValueError("duplicate or unbound run")
        seen.add(x["run_id"]); jobs.add(x["job_id"])
        if not isinstance(x["head_sha"],str) or not SHA.fullmatch(x["head_sha"]):
            raise ValueError("invalid source pin syntax")
        if (x["runner"]!=OS or x["stage"]!=STAGE or
            x["terminal_marker"]!=SIGNATURE or
            type(x["host_exit_code"]) is not int or x["host_exit_code"]!=70 or
            x["conclusion"]!="failure" or
            x["evidence_kind"]!="public-gha-job-log-observation"):
            raise ValueError("not a matching read-only failure observation")
        heads.add(x["head_sha"])
        verified.append({"run_id":x["run_id"],"head_sha":x["head_sha"],
                         "job_id":x["job_id"],"result":"HARD_FAIL_HELD"})
    unique_heads=len(heads)
    # Distinct source SHAs are not independent causal experiments. This
    # offline critic validates manifest structure, not GitHub authenticity.
    # External root cause remains UNKNOWN.
    return {
        "schema":"suprachat-uia-lineage-critic/v1",
        "classification":("RECURRING_CROSS_HEAD_FAILURE" if unique_heads>=3
                          else "INSUFFICIENT_INDEPENDENT_HEADS"),
        "unique_source_heads":unique_heads,
        "observation_count":len(verified),
        "terminal_marker":SIGNATURE,
        "exit_code":70,
        "root_cause":"UNKNOWN",
        "installation_or_uia_gate_waived":False,
        "real_desktop_hil_passed":False,
        "source_modifications":False,
        "auto_retry_authorized":False,
        "auto_merge_authorized":False,
        "observations":sorted(verified,key=lambda r:r["run_id"]),
    }
def unique_object(pairs):
    result={}
    for key,value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key]=value
    return result

def load_document(content):
    if len(content)>65536:raise ValueError("evidence manifest oversized")
    return json.loads(content.decode("utf-8"),object_pairs_hook=unique_object)

def main():
    p=argparse.ArgumentParser()
    p.add_argument("manifest",type=Path)
    a=p.parse_args()
    content=a.manifest.read_bytes()
    doc=load_document(content)
    print(json.dumps(critic(doc),sort_keys=True))
if __name__=="__main__":main()
