#!/usr/bin/env python3
"""R48 offline bounded parallel-workset critic, Python stdlib only.

This is a plan and evidence validator, not a job scheduler, remote executor,
secret broker, GitHub write client, or source of authorization.
The private DLE remains authoritative and is never pulled into public CI.
"""
from __future__ import annotations
import json
import re
import sys
from pathlib import Path

SCHEMA = "suprachat-parallel-workset/v1"
STATES = {"READY", "RUNNING", "HELD", "BLOCKED", "DONE"}
EXECUTORS = {"public-gha", "local-qualified", "private-governed"}
AUDIENCES = {"human", "automation", "agent", "accessible-human"}
ID = re.compile(r"^[a-z][a-z0-9-]{2,45}$")
PATH = re.compile(r"^[A-Za-z0-9._/-]{1,160}$")

def require(ok: bool, code: str) -> None:
    if not ok:
        raise ValueError(code)

def safe_path(s: object) -> bool:
    return (isinstance(s, str) and bool(PATH.fullmatch(s)) and
            not s.startswith("/") and not s.endswith("/") and
            not any(part in ("", ".", "..") for part in s.split("/")))

def independent(a: dict, b: dict) -> bool:
    if a["repo"] != b["repo"]:
        return True
    # Both paths and directory roots are exclusive; no parallel writes to
    # the same tree or cross-task rebases on a shared workspace.
    return all(not (p == q or p.startswith(q + "/") or q.startswith(p + "/"))
               for p in a["write_paths"] for q in b["write_paths"])

def validate(doc: object) -> dict:
    require(isinstance(doc, dict) and set(doc) == {
        "schema", "workset_id", "authority", "budget", "tasks", "integration_gates"},
        "PLAN_SCHEMA")
    require(doc["schema"] == SCHEMA and isinstance(doc["workset_id"], str) and
            ID.fullmatch(doc["workset_id"]) is not None, "PLAN_ID")
    a = doc["authority"]
    require(isinstance(a, dict) and set(a) == {
        "public_repo", "private_dle", "owner_issue",
        "human_can_preempt", "single_session_authority", "owner_controls_merge"},
        "AUTHORITY_SCHEMA")
    require(a["public_repo"] == "SemperSupra/suprachat" and
            a["private_dle"] == "SemperSupra/suprachat-private#2" and
            a["owner_issue"] == "SemperSupra/suprachat#195" and
            all(a[k] is True for k in ("human_can_preempt",
                  "single_session_authority", "owner_controls_merge")),
            "AUTHORITY_BOUNDARY")
    budget = doc["budget"]
    require(isinstance(budget, dict) and set(budget) == {
        "max_concurrent", "max_public_gha_jobs", "private_gha_minutes",
        "paid_usd", "recursive_delegation"}, "BUDGET_SCHEMA")
    require(type(budget["max_concurrent"]) is int and 2 <= budget["max_concurrent"] <= 4 and
            type(budget["max_public_gha_jobs"]) is int and
            1 <= budget["max_public_gha_jobs"] <= 2 and
            budget["private_gha_minutes"] == 0 and budget["paid_usd"] == 0 and
            budget["recursive_delegation"] is False, "COST_OR_DELEGATION_BOUNDARY")
    tasks = doc["tasks"]
    require(isinstance(tasks, list) and 2 <= len(tasks) <= 24, "WORKSET_SIZE")
    lookup = {}
    for t in tasks:
        require(isinstance(t, dict) and set(t) == {
            "id","repo","lane","state","executor","depends_on","write_paths",
            "acceptance_oracle","evidence_receipt","human_override_response",
            "audiences","blocker"}, "TASK_SCHEMA")
        tid = t["id"]
        require(isinstance(tid, str) and ID.fullmatch(tid) is not None and
                tid not in lookup, "TASK_ID")
        lookup[tid] = t
        require(t["repo"] in ("SemperSupra/suprachat",
                              "SemperSupra/suprachat-private",
                              "SemperSupra/android-publishing-foundry",
                              "SemperSupra/model-spelunker") and
                isinstance(t["lane"], str) and ID.fullmatch(t["lane"]) is not None,
                "TASK_OWNER")
        require(t["state"] in STATES and t["executor"] in EXECUTORS, "TASK_STATE")
        require(isinstance(t["depends_on"], list) and
                len(t["depends_on"]) == len(set(t["depends_on"])) and
                all(isinstance(d, str) and ID.fullmatch(d) for d in t["depends_on"]),
                "DEPENDENCIES")
        require(isinstance(t["write_paths"], list) and t["write_paths"] and
                all(safe_path(p) for p in t["write_paths"]) and
                len(set(t["write_paths"])) == len(t["write_paths"]), "PATHS")
        require(isinstance(t["acceptance_oracle"], str) and
                12 <= len(t["acceptance_oracle"]) <= 240 and
                isinstance(t["evidence_receipt"], str) and
                5 <= len(t["evidence_receipt"]) <= 240, "MISSING_ORACLE")
        require(t["human_override_response"] == "FENCE_AND_RECONCILE",
                "HUMAN_PREEMPTION_LOST")
        require(isinstance(t["audiences"], list) and set(t["audiences"]) ==
                AUDIENCES, "AUDIENCE_PARITY")
        require((t["state"] in ("HELD", "BLOCKED")) ==
                (isinstance(t["blocker"], str) and len(t["blocker"]) >= 10),
                "BLOCKER_STATE")
        if t["repo"].endswith("-private"):
            require(t["executor"] != "public-gha", "PRIVATE_SOURCE_PUBLIC_GHA")
        if t["state"] in ("READY", "RUNNING") and t["executor"] == "private-governed":
            require(False, "PRIVATE_RUNNER_NOT_ADMITTED")
    for t in tasks:
        require(all(x in lookup and x != t["id"] for x in t["depends_on"]),
                "UNKNOWN_DEPENDENCY")
        if t["state"] in ("READY", "RUNNING"):
            require(all(lookup[d]["state"] == "DONE" for d in t["depends_on"]),
                    "DEPENDENT_TASK_SCHEDULED_TOO_EARLY")
    visited, active = set(), set()
    def walk(tid: str) -> None:
        if tid in active:
            raise ValueError("DEPENDENCY_CYCLE")
        if tid in visited:
            return
        active.add(tid)
        for dep in lookup[tid]["depends_on"]:
            walk(dep)
        active.remove(tid)
        visited.add(tid)
    for tid in lookup:
        walk(tid)

    runnable = [t for t in tasks if t["state"] in ("READY", "RUNNING")]
    require(len(runnable) >= 2, "SERIAL_ONLY_ANTI_PATTERN")
    require(len(runnable) <= budget["max_concurrent"],
            "CONCURRENT_CAPACITY_EXCEEDED")
    for i, a in enumerate(runnable):
        for b in runnable[i+1:]:
            require(independent(a, b), "WRITE_PATH_COLLISION")
            require(a["id"] not in b["depends_on"] and
                    b["id"] not in a["depends_on"], "ACTIVE_DEPENDENCY_CONFLICT")

    gates = doc["integration_gates"]
    require(isinstance(gates, list) and gates == [
        "independent-source-and-oracle",
        "exact-head-public-ci",
        "cross-lane-peer-review",
        "owner-approved-merge",
        "durable-dle-reconciliation"], "INTEGRATION_ORDER")
    return {
        "schema": "suprachat-parallel-workset-critic/v1",
        "result": "PASS",
        "runnable": sorted(t["id"] for t in runnable),
        "blocked": sorted(t["id"] for t in tasks if t["state"] in ("BLOCKED","HELD")),
        "concurrent": len(runnable),
        "private_source_in_public_ci": False,
        "tool_authority_granted": False,
        "work_executed": False,
    }

def main() -> None:
    import argparse
    p=argparse.ArgumentParser()
    p.add_argument("manifest", type=Path)
    args=p.parse_args()
    result=validate(json.loads(args.manifest.read_text(encoding="utf-8")))
    print(json.dumps(result,sort_keys=True))
if __name__ == "__main__":
    main()
