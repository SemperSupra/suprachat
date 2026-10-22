#!/usr/bin/env python3
"""W7 disposable SQLite durable first-outcome/restart/crash qualification."""
import json
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import uuid

DLL = str(pathlib.Path(sys.argv[1]).resolve())


def launch(store):
    p = subprocess.Popen(["dotnet", DLL, "serve", str(store)],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
    ready = p.stdout.readline()
    assert ready, "host failed to admit: " + p.stderr.read()[:300]
    receipt = json.loads(ready)
    assert receipt["ready"] and receipt["sqlite_wal_full"]
    assert not receipt["provider_contacted"] and not receipt["actor_identity_authenticated"]
    assert not receipt["production_host_integrated"]
    return p


def send(process, req, expect_exit=None):
    process.stdin.write(json.dumps(req, separators=(",", ":")) + "\n")
    process.stdin.flush()
    if expect_exit is not None:
        assert process.wait(timeout=8) == expect_exit, ("unexpected exit", process.returncode)
        return None
    reply = process.stdout.readline()
    assert reply, "missing receipt or unexpected host exit"
    return json.loads(reply)


def close(p):
    if p.poll() is None:
        try:
            p.stdin.close()
            p.wait(timeout=8)
        except (BrokenPipeError, subprocess.TimeoutExpired):
            p.kill()
            p.wait(timeout=8)
    p.stdout.close()
    p.stderr.close()


def request(kind, key, title, **overrides):
    return dict(action=kind, operationId=key, audience="agent",
                title=title, **overrides)


def main():
    with tempfile.TemporaryDirectory(prefix="suprachat_w7_") as tmp:
        store = pathlib.Path(tmp) / "rdte.sqlite"
        p = launch(store)
        try:
            initial = request("open", "w7-create-session-00001", "synthetic-A")
            a = send(p, initial)
            assert a["Applied"] and a["Status"] == "APPLIED"
            sid = a["Session"]["Id"]
            duplicate = send(p, initial)
            assert duplicate["IsReplay"] and duplicate["Sequence"] == a["Sequence"]
            changed = send(p, request("open", "w7-create-session-00001", "wrong-payload"))
            assert changed["Status"] == "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT"
            b = send(p, request("open", "w7-create-session-00002", "synthetic-B"))
            assert a["Session"]["Id"] != b["Session"]["Id"]

            update = request("change", "w7-update-A-00000001", "A-new",
                             sessionId=sid, expectedVersion=0)
            ok = send(p, update)
            assert ok["Applied"] and ok["Session"]["Version"] == 1
            stale = request("change", "w7-reject-stale-00001", "wrong-version",
                            sessionId=sid, expectedVersion=0)
            failure = send(p, stale)
            assert failure["Status"] == "VERSION_CONFLICT" and not failure["Applied"]
            assert send(p, stale)["IsReplay"]
            second_b = request("change", "w7-update-B-00000001", "B-progress",
                               sessionId=b["Session"]["Id"], expectedVersion=0)
            assert send(p, second_b)["Applied"]
            first_integrity = send(p, {"action": "integrity"})
            assert first_integrity["integrity"] == "PASS"
            assert first_integrity["outcomes"] == 5
        finally:
            close(p)

        # Two-host ownership must be refused even when the SQL database is valid.
        p = launch(store)
        contender = subprocess.run(["dotnet", DLL, "serve", str(store)],
                                   capture_output=True, text=True, timeout=8)
        assert contender.returncode == 78 and '"ready":true' not in contender.stdout
        assert send(p, initial)["IsReplay"]
        assert send(p, stale)["IsReplay"]
        assert send(p, {"action": "list"})["Sessions"][1]["Version"] in (0, 1)
        close(p)

        # Repeated crash points. Before commit: NO receipt/side effect.
        # After commit before ACK: original receipt replays, not a new side effect.
        total = 100
        for i in range(total):
            key = f"w7-crash-cycle-{i:05d}"
            action = request("change", key, f"crash-{i:03d}",
                             sessionId=sid, expectedVersion=1 + i)
            action["fault"] = "BEFORE_COMMIT" if i % 2 == 0 else "AFTER_COMMIT_BEFORE_REPLY"
            p = launch(store)
            try:
                send(p, action, expect_exit=88 if i % 2 == 0 else 89)
            finally:
                close(p)
            p = launch(store)
            try:
                retry = dict(action)
                retry["fault"] = None
                outcome = send(p, retry)
                assert outcome["Applied"], outcome
                assert outcome["IsReplay"] == (i % 2 != 0), (i, outcome)
                again = send(p, retry)
                assert again["IsReplay"] and again["Sequence"] == outcome["Sequence"]
                if i == 0 or i == total - 1 or i % 20 == 0:
                    integ = send(p, {"action": "integrity"})
                    assert integ["sequence"] == integ["outcomes"] == integ["events"]
            finally:
                close(p)

        p = launch(store)
        try:
            final = send(p, {"action": "integrity"})
            assert final["sequence"] == 105, final
            assert final["sessions"] == 2, final
            after = send(p, {"action": "inspect", "sessionId": sid})
            assert after["Session"]["Version"] == 101, after
            assert send(p, stale)["IsReplay"]
            assert send(p, update)["IsReplay"]
        finally:
            close(p)

        # Corrupt schema must FAIL CLOSED (no empty-state fallback).
        invalid_version = pathlib.Path(tmp) / "wrong-version.sqlite"
        db = sqlite3.connect(str(invalid_version))
        try:
            with db:
                db.executescript("""
                  CREATE TABLE ledger_meta(singleton INTEGER PRIMARY KEY,
                   schema_version INTEGER NOT NULL,next_sequence INTEGER NOT NULL);
                  INSERT INTO ledger_meta VALUES(1,999,0);
                """)
        finally:
            # Python sqlite3 connection's context manager commits but DOES NOT
            # close handles. Windows will refuse subsequent file deletion.
            db.close()
        v = subprocess.run(["dotnet", DLL, "serve", str(invalid_version)],
                           capture_output=True, text=True, timeout=8)
        assert v.returncode == 78 and '"ready":true' not in v.stdout
        # File release is an independent correctness oracle on Windows.
        invalid_version.unlink()

        corrupt = pathlib.Path(tmp) / "corrupt.sqlite"
        corrupt.write_bytes(b"not a sqlite store\0" * 256)
        z = subprocess.run(["dotnet", DLL, "serve", str(corrupt)],
                           capture_output=True, text=True, timeout=8)
        assert z.returncode == 78 and '"ready":true' not in z.stdout
        corrupt.unlink()

        print(json.dumps({
            "schema": "suprachat-w7-durable-rdte-receipt/v1",
            "result": "PASS",
            "platform": sys.platform,
            "crash_restart_reps": total,
            "durable_sequence": final["sequence"],
            "sessions_after_crash": final["sessions"],
            "same_key_success_replay": True,
            "rejected_outcome_replay": True,
            "conflicting_key_payload_denied": True,
            "single_owner_exclusion": True,
            "corrupt_schema_fail_closed": True,
            "corrupt_db_fail_closed": True,
            "provider_calls": 0,
            "w6_host_integrated": False,
            "credential_identity_authenticated": False
        }))
if __name__ == "__main__":
    main()
