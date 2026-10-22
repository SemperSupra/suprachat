#!/usr/bin/env python3
"""Zero-provider, three-audience, multiprocess, two-session causal qualification."""
import concurrent.futures
import json
import pathlib
import subprocess
import sys
import tempfile
import time
import uuid

DLL = str(pathlib.Path(sys.argv[1]).resolve())
PIPE = "suprachat_rdte_" + uuid.uuid4().hex[:18]


def launch(store, name=PIPE):
    p = subprocess.Popen(["dotnet", DLL, "serve", "--pipe", name, str(store)],
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                         text=True, encoding="utf-8")
    line = p.stdout.readline()
    if not line:
        raise AssertionError("host did not become ready: " + p.stderr.read()[:500])
    ready = json.loads(line)
    assert ready["ready"] and ready["phase"] == "SYNTHETIC_LOCAL_RDTE", ready
    return p


def call(action, **kwargs):
    request = {"action": action, **kwargs}
    outcome = subprocess.run(["dotnet", DLL, "call", PIPE, json.dumps(request)],
                             capture_output=True, text=True, encoding="utf-8", timeout=15)
    assert outcome.returncode == 0, (action, outcome.stderr)
    return json.loads(outcome.stdout.strip())


def passed(response):
    assert response["ok"], response
    return response["result"]


def refused(response, expected):
    assert not response["ok"] and response["error"] == expected, response


def shutdown(process):
    process.terminate()
    try:
        process.wait(timeout=8)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=8)
    process.stdout.close()
    process.stderr.close()


def main():
    with tempfile.TemporaryDirectory(prefix="suprachat_shared_sessions_") as tmp:
        store = pathlib.Path(tmp) / "synthetic-sessions.json"
        proc = launch(store)
        try:
            capabilities = passed(call("capabilities/read"))
            assert not capabilities["provider_chat_submit"], capabilities
            assert not capabilities["authenticated_actor_authority"], capabilities

            a = passed(call("session/create", title="Synthetic A",
                            actor_id="operator", audience="human", operation_id="create-A"))
            b = passed(call("session/create", title="Synthetic B",
                            actor_id="worker-B", audience="automation",
                            operation_id="create-B"))
            a_id, b_id = a["id"], b["id"]
            assert a_id != b_id
            assert len(passed(call("session/list"))) == 2

            same_create = call("session/create", title="Synthetic A", actor_id="operator",
                               audience="human", operation_id="create-A")
            assert same_create["ok"] and same_create["replayed"]
            assert same_create["result"]["id"] == a_id
            refused(call("session/create", title="Different", actor_id="operator",
                         audience="human", operation_id="create-A"), "IDEMPOTENCY_CONFLICT")

            passed(call("session/join", session_id=a_id,
                        actor_id="automation-A", audience="automation"))
            passed(call("session/join", session_id=a_id,
                        actor_id="agent-A", audience="agent"))
            refused(call("session/join", session_id=a_id,
                         actor_id="agent-A", audience="human"), "PARTICIPANT_CONFLICT")
            a_before = passed(call("session/read", session_id=a_id))["revision"]
            b_before = passed(call("session/read", session_id=b_id))["revision"]
            assert a_before == 3 and b_before == 1

            # Separate OS processes simultaneously make conflicting A writes
            # and an independent B write. A has precisely one winner.
            with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
                futures = [
                    pool.submit(call, "session/append", session_id=a_id,
                                actor_id="automation-A", operation_id="a-automated",
                                kind="NOTE", detail="safe-metadata-A1",
                                expected_revision=a_before),
                    pool.submit(call, "session/append", session_id=a_id,
                                actor_id="agent-A", operation_id="a-agent",
                                kind="NOTE", detail="safe-metadata-A2",
                                expected_revision=a_before),
                    pool.submit(call, "session/append", session_id=b_id,
                                actor_id="worker-B", operation_id="b-worker",
                                kind="NOTE", detail="safe-metadata-B1",
                                expected_revision=b_before)
                ]
                results = [f.result(timeout=25) for f in futures]
            assert sum(x["ok"] for x in results[:2]) == 1, results
            assert {x.get("error") for x in results[:2] if not x["ok"]} == {"REVISION_CONFLICT"}
            assert results[2]["ok"], results[2]

            winner = results[0] if results[0]["ok"] else results[1]
            winner_op = "a-automated" if results[0]["ok"] else "a-agent"
            winner_actor = "automation-A" if results[0]["ok"] else "agent-A"
            winner_detail = "safe-metadata-A1" if results[0]["ok"] else "safe-metadata-A2"
            replay = call("session/append", session_id=a_id,
                          actor_id="operator", operation_id=winner_op,
                          kind="NOTE", detail=winner_detail, expected_revision=0)
            assert replay["ok"] and replay["replayed"], replay
            assert replay["result"]["revision"] == winner["result"]["revision"]
            refused(call("session/append", session_id=a_id,
                         actor_id=winner_actor, operation_id=winner_op,
                         kind="NOTE", detail="DIFFERENT", expected_revision=a_before),
                    "IDEMPOTENCY_CONFLICT")
            refused(call("session/append", session_id=a_id,
                         actor_id="unknown", operation_id="unauthorized",
                         kind="NOTE", detail="safe", expected_revision=4),
                    "ACTOR_NOT_JOINED")

            for actor in ("operator", "automation-A", "agent-A"):
                view = passed(call("session/read", session_id=a_id))
                assert view["revision"] == 4 and len(view["participants"]) == 3
            a_events = passed(call("session/events", session_id=a_id, after_revision=3))
            assert [e["revision"] for e in a_events["events"]] == [4]
            assert a_events["events"][0]["operation_id"] == winner_op

            # Model a lost provider ACK without ANY provider call. Quarantine A,
            # not B; retry cannot send a duplicate, even after host restart.
            uncertainty = passed(call("session/append", session_id=a_id,
                                       actor_id="agent-A", operation_id="a-uncertain",
                                       kind="UNCERTAIN_INTENT_SIMULATION",
                                       detail="NO-PROVIDER-CALL", expected_revision=4))
            assert uncertainty["status"] == "UNCERTAIN_SIMULATED_NO_PROVIDER_CALL"
            refused(call("session/append", session_id=a_id, actor_id="operator",
                         operation_id="blocked", kind="NOTE", detail="blocked",
                         expected_revision=5), "SESSION_QUARANTINED")
            passed(call("session/append", session_id=b_id, actor_id="worker-B",
                        operation_id="b-continues", kind="NOTE",
                        detail="B-progresses", expected_revision=2))
            assert passed(call("session/read", session_id=b_id))["revision"] == 3

            # A second host must not share the same store ownership.
            contender = subprocess.run(["dotnet", DLL, "serve", "--pipe",
                                        "suprachat_contender_" + uuid.uuid4().hex[:12],
                                        str(store)], timeout=8, capture_output=True, text=True)
            assert contender.returncode != 0 and "ready" not in contender.stdout.lower()

            shutdown(proc)
            assert store.is_file(), "durable local snapshot missing"
            proc = launch(store)
            assert passed(call("session/read", session_id=a_id))["quarantined"]
            assert passed(call("session/read", session_id=b_id))["revision"] == 3
            replay_after_restart = call("session/append", session_id=a_id,
                                        actor_id="automation-A", operation_id="a-uncertain",
                                        kind="UNCERTAIN_INTENT_SIMULATION",
                                        detail="NO-PROVIDER-CALL", expected_revision=0)
            assert replay_after_restart["ok"] and replay_after_restart["replayed"]
            assert len(passed(call("session/list"))) == 2
            b_events = passed(call("session/events", session_id=b_id, after_revision=1))
            assert [e["revision"] for e in b_events["events"]] == [2, 3]
            print(json.dumps({
                "result": "PASS", "phase": "SYNTHETIC_LOCAL_RDTE",
                "independent_sessions": 2,
                "concurrent_audiences_in_A": 3,
                "native_client_processes": True,
                "per_session_revision_fence": "PASS",
                "cross_client_idempotent_replay": "PASS",
                "restart_and_uncertainty_quarantine": "PASS",
                "independent_B_progress": "PASS",
                "provider_requests_sent": 0,
                "terminal_commands_executed": 0
            }))
        finally:
            shutdown(proc)


if __name__ == "__main__":
    main()
