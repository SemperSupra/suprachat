#!/usr/bin/env python3
"""R49 synthetic negative mutants against redacted public exact-head observations."""
from __future__ import annotations
import copy
import json
import runpy
from pathlib import Path

BASE=Path(__file__).parent
module=runpy.run_path(str(BASE/"classify_uia.py"))
critic=module["critic"]
load_document=module["load_document"]
FIX=BASE/"fixtures"/"three_public_heads.json"
def reject(data):
    try:critic(data)
    except (ValueError,TypeError,KeyError):return
    raise AssertionError("unsafe or unverifiable UIA failure observation accepted")

def main():
    base=load_document(FIX.read_bytes())
    out=critic(base)
    assert out["classification"]=="RECURRING_CROSS_HEAD_FAILURE"
    assert out["unique_source_heads"]==7
    assert out["observation_count"]==7
    assert out["root_cause"]=="UNKNOWN"
    assert not any(out[k] for k in (
        "installation_or_uia_gate_waived","real_desktop_hil_passed",
        "source_modifications","auto_retry_authorized","auto_merge_authorized"))
    cases=[]
    def mutate(fn):
        b=copy.deepcopy(base);fn(b);cases.append(b)
    mutate(lambda d:d["observations"][1].update(head_sha="main"))
    mutate(lambda d:d["observations"][0].update(host_exit_code=0))
    mutate(lambda d:d["observations"][0].update(host_exit_code=True))
    mutate(lambda d:d["observations"][0].update(terminal_marker="PASS"))
    mutate(lambda d:d["observations"][1].update(runner="ubuntu-24.04"))
    mutate(lambda d:d["observations"][0].update(stage="other"))
    mutate(lambda d:d["observations"][2].update(conclusion="success"))
    mutate(lambda d:d["observations"][0].update(evidence_kind="raw-debug"))
    mutate(lambda d:d["observations"][0].update(access_token="not-a-token"))
    mutate(lambda d:d["observations"][0].update(run_id=d["observations"][1]["run_id"]))
    mutate(lambda d:d.update(repository="SemperSupra/suprachat-private"))
    mutate(lambda d:d.update(automatic_ci_waiver=True))
    mutate(lambda d:d["observations"][1].update(job_id=-1))
    mutate(lambda d:d["observations"][1].update(job_id=d["observations"][0]["job_id"]))
    mutate(lambda d:d["observations"][1].update(job_id=True))
    mutate(lambda d:d["observations"][1].update(head_sha="g"*40))
    for b in cases:reject(b)
    repeated=copy.deepcopy(base)
    for observation in repeated["observations"]:
        observation["head_sha"]=repeated["observations"][0]["head_sha"]
    v=critic(repeated)
    assert v["classification"]=="INSUFFICIENT_INDEPENDENT_HEADS"
    assert v["unique_source_heads"]==1
    for raw in (b'{"schema":1,"schema":2}',
                b'{"observations":[{"run_id":1,"run_id":2}]}',
                b' ' * 65537):
        try:
            load_document(raw)
        except ValueError:
            pass
        else:
            raise AssertionError("ambiguous or oversized JSON accepted")
    print(json.dumps({"schema":"suprachat-r49-uia-lineage-test/v1",
       "result":"PASS","negative_cases":len(cases)+3,
       "actual_desktop_repaired":False,"ci_waiver":False,
       "root_cause":"UNKNOWN"},sort_keys=True))
if __name__=="__main__":main()
