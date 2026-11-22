#!/usr/bin/env python3
"""R48 independent critic tests, stdlib only; no network, repo mutation or secrets."""
from __future__ import annotations
import copy
import json
from pathlib import Path
from tools.qualification.validate_parallel_workset import validate, bounded_actor_cards

FIXTURE=Path(__file__).parent/"fixtures"/"parallel_workset_synthetic.json"

def reject(plan, code):
    try:
        validate(plan)
    except ValueError as e:
        assert code in str(e), (code, str(e))
        return
    raise AssertionError("UNSAFE_PLAN_ADMITTED:"+code)

def main():
    base=json.loads(FIXTURE.read_text(encoding="utf-8"))
    outcome=validate(base)
    assert outcome["concurrent"]==2
    assert len(outcome["runnable"])==2
    assert outcome["blocked"]==["protected-hil-fence"]
    assert outcome["private_source_in_public_ci"] is False
    assert outcome["tool_authority_granted"] is False
    cards=bounded_actor_cards(base)
    assert cards["parallel_count"]==2
    assert len(cards["cards"])==2
    assert cards["grants_execution_authority"] is False
    assert cards["no_private_github_actions"] is True
    for card in cards["cards"]:
        assert len(card["base_sha"])==40
        assert card["branch"].startswith("agent/")
        assert card["oracle_command"]
        assert card["negative_case"]
        assert "secret" not in json.dumps(card).lower()


    def change(fn):
        v=copy.deepcopy(base)
        fn(v)
        return v
    cases=[
       (lambda x:x["tasks"][1].update(write_paths=["tools/qualification"]), "WRITE_PATH_COLLISION"),
       (lambda x:x["tasks"][1].update(write_paths=["tools"]), "WRITE_PATH_COLLISION"),
       (lambda x:x["tasks"][1].update(depends_on=["actor-contract-critic"]), "DEPENDENT_TASK_SCHEDULED_TOO_EARLY"),
       (lambda x:x["tasks"][2].update(state="READY",blocker=None), "DEPENDENT_TASK_SCHEDULED_TOO_EARLY"),
       (lambda x:x["tasks"][0].update(source_branch="evil:escape"), "SOURCE_BRANCH_REQUIRED"),
       (lambda x:x["tasks"][0].update(base_sha="unpinned"), "TASK_PIN"),
       (lambda x:x["tasks"][0].update(oracle_command="rm -rf /; echo pass"), "UNSAFE_ORACLE_COMMAND"),
       (lambda x:x["tasks"][0].update(negative_case="none"), "NEGATIVE_CASE_MISSING"),
       (lambda x:x["tasks"][0].update(state="DONE"), "SERIAL_ONLY_ANTI_PATTERN"),
       (lambda x:x["tasks"][0].update(state="HELD",blocker="Lack of qualified actor capacity"), "SERIAL_ONLY_ANTI_PATTERN"),
       (lambda x:x["tasks"][0].update(acceptance_oracle="okay"), "MISSING_ORACLE"),
       (lambda x:x["tasks"][0].update(audiences=["agent"]), "AUDIENCE_PARITY"),
       (lambda x:x["tasks"][0].update(human_override_response="IGNORE"), "HUMAN_PREEMPTION_LOST"),
       (lambda x:x["tasks"][0].update(repo="SemperSupra/suprachat-private"), "PRIVATE_SOURCE_PUBLIC_GHA"),
       (lambda x:x["budget"].update(private_gha_minutes=1), "COST_OR_DELEGATION_BOUNDARY"),
       (lambda x:x["budget"].update(paid_usd=10), "COST_OR_DELEGATION_BOUNDARY"),
       (lambda x:x["budget"].update(recursive_delegation=True), "COST_OR_DELEGATION_BOUNDARY"),
       (lambda x:x["authority"].update(owner_controls_merge=False), "AUTHORITY_BOUNDARY"),
       (lambda x:x["tasks"][0].update(write_paths=["../secrets"]), "PATHS"),
       (lambda x:x["tasks"][2].update(depends_on=["unknown-task"]), "UNKNOWN_DEPENDENCY"),
       (lambda x:x["tasks"][0].update(depends_on=["protected-hil-fence"]), "DEPENDENT_TASK_SCHEDULED_TOO_EARLY"),
       (lambda x:x["tasks"][2].update(depends_on=["protected-hil-fence"]), "UNKNOWN_DEPENDENCY"),
       (lambda x:x["tasks"][0].update(id="actor-bootstrap-baton"), "TASK_ID"),
       (lambda x:x.update(integration_gates=["owner-approved-merge","exact-head-public-ci"]), "INTEGRATION_ORDER"),
    ]
    for mutation, code in cases:
        reject(change(mutation),code)
    print(json.dumps({"schema":"suprachat-r48-batch-critic-test/v1",
                      "result":"PASS","negative_cases":len(cases),
                      "runnable":outcome["runnable"],"actual_workers_dispatched":False,
                      "private_source_uploaded":False,
                      "protected_host_mutated":False},sort_keys=True))
if __name__=="__main__":
    main()
