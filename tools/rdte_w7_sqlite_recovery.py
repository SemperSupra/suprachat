#!/usr/bin/env python3
"""W7 crash-consistency RDTE using Python's built-in SQLite (not a W6 host adapter).

Runs only synthetic fixtures in a new owned disposable directory on public CI.
Records actual interprocess ACID behavior; not a production storage admission.
"""
import hashlib
import json
import os
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import time
import uuid

SCHEMA = "suprachat-w7-sqlite-durable-reference/v1"
VERSION = 7
MAX_REPS = 100
SEED = 20261022

def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=True, separators=(",", ":"))

def connect(db, *, create=False):
    db = pathlib.Path(db)
    if not db.parent.name.startswith("suprachat-w7-owned-"):
        raise RuntimeError("UNOWNED_DATABASE_PATH_REFUSED")
    if not create and not db.is_file():
        raise RuntimeError("MISSING_DATABASE_FAIL_CLOSED")
    cn = sqlite3.connect(str(db), timeout=.25, isolation_level=None)
    try:
        cn.execute("PRAGMA busy_timeout=250")
        cn.execute("PRAGMA synchronous=FULL")
        if create:
            cn.execute("PRAGMA journal_mode=WAL")
            cn.executescript("""
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    version INTEGER NOT NULL CHECK(version >= 0));
                CREATE TABLE IF NOT EXISTS outcomes (
                    key TEXT PRIMARY KEY,
                    actor TEXT NOT NULL,
                    payload_hash TEXT NOT NULL,
                    value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    action TEXT NOT NULL,
                    version INTEGER NOT NULL);
                PRAGMA user_version=7;
            """)
        if cn.execute("PRAGMA user_version").fetchone()[0] != VERSION:
            raise RuntimeError("UNRECOGNIZED_SCHEMA_FAIL_CLOSED")
        if cn.execute("PRAGMA quick_check").fetchone()[0] != "ok":
            raise RuntimeError("CORRUPTION_FAIL_CLOSED")
        expected = {"sessions", "outcomes", "events"}
        found = {r[0] for r in cn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if not expected <= found:
            raise RuntimeError("TABLES_MISSING_FAIL_CLOSED")
        return cn
    except BaseException:
        cn.close()
        raise

def apply(db, op, *, crash=None):
    # The same SQLite BEGIN IMMEDIATE protects the session revision, event
    # append and BOTH accepted and rejected operation outcome. No ACK until COMMIT.
    cn = connect(db)
    try:
        actor = op["actor"]
        key = op["key"]
        if actor not in ("human", "agent", "automation") or not (12 <= len(key) <= 120):
            raise ValueError("INVALID_SYNTHETIC_OPERATION")
        fingerprint = hashlib.sha256(canonical(op).encode("utf-8")).hexdigest()
        if crash == "before_begin":
            os._exit(82)
        cn.execute("BEGIN IMMEDIATE")
        previous = cn.execute(
            "SELECT actor,payload_hash,value FROM outcomes WHERE key=?", (key,)
        ).fetchone()
        if previous is not None:
            if previous[0] != actor or previous[1] != fingerprint:
                result = {"status":"IDEMPOTENCY_KEY_PAYLOAD_CONFLICT","applied":False}
            else:
                result = dict(json.loads(previous[2]), replay=True)
            cn.execute("COMMIT")
            return result

        if op["kind"] == "open":
            sid = uuid.uuid4().hex
            cn.execute("INSERT INTO sessions VALUES(?,?,0)", (sid,op["title"]))
            version = 0
            status = "APPLIED"
            applied = True
        elif op["kind"] == "change":
            sid = op["session"]
            row = cn.execute("SELECT version FROM sessions WHERE id=?", (sid,)).fetchone()
            version = row[0] if row else -1
            if row is None: status,applied = "NOT_FOUND",False
            elif version != op["expected"]: status,applied = "VERSION_CONFLICT",False
            else:
                version += 1
                cn.execute("UPDATE sessions SET title=?,version=? WHERE id=?",
                           (op["title"],version,sid))
                status,applied = "APPLIED",True
        else:
            raise ValueError("UNSUPPORTED_SYNTHETIC_KIND")
        if applied:
            cn.execute("INSERT INTO events(session_id,actor,action,version) VALUES(?,?,?,?)",
                       (sid,actor,op["kind"],version))
        sequence = cn.execute("SELECT COALESCE(MAX(sequence),0) FROM events").fetchone()[0]
        result = {"status":status,"applied":applied,"session":sid,
                  "version":version,"sequence":sequence,"replay":False}
        cn.execute("INSERT INTO outcomes VALUES(?,?,?,?)",
                   (key,actor,fingerprint,canonical(result)))
        if crash == "after_write_before_commit":
            os._exit(83)
        cn.execute("COMMIT")
        if crash == "after_commit_before_ack":
            os._exit(84)
        return result
    finally:
        cn.close()

def inspect(db):
    cn = connect(db)
    try:
        return {
            "sessions":cn.execute("SELECT id,title,version FROM sessions ORDER BY id").fetchall(),
            "events":cn.execute("SELECT sequence,session_id,actor,action,version FROM events ORDER BY sequence").fetchall(),
            "outcomes":cn.execute("SELECT key,actor,payload_hash,value FROM outcomes ORDER BY key").fetchall()
        }
    finally:
        cn.close()

def subprocess_worker(db,op,crash="none"):
    command = [sys.executable, __file__, "worker",str(db),canonical(op),crash]
    p=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,
                     timeout=8,check=False)
    if len(p.stdout)>4096 or len(p.stderr)>4096:
        raise AssertionError("UNBOUNDED_WORKER_OUTPUT")
    return p.returncode, json.loads(p.stdout) if p.stdout.strip() else None

def worker(argv):
    if len(argv) != 3: raise RuntimeError("WORKER_ARGUMENTS_INVALID")
    db,raw,crash=argv
    if crash not in ("none","before_begin","after_write_before_commit",
                     "after_commit_before_ack"):
        raise RuntimeError("UNRECOGNIZED_FAULT_POINT")
    result=apply(db,json.loads(raw),crash=crash)
    print(canonical(result))

def require(condition, message):
    if not condition:
        raise AssertionError(message)

def selftest():
    if os.environ.get("SUPRACHAT_W7_PUBLIC_FIXED_FIXTURE") != "yes":
        raise RuntimeError("EXPLICIT_DISPOSABLE_FIXTURE_ADMISSION_REQUIRED")
    outcomes=[]
    def passed(name,extra=None):
        outcomes.append({"test":name,"result":"PASS",**(extra or {})})
    with tempfile.TemporaryDirectory(prefix="suprachat-w7-owned-") as owned:
        root=pathlib.Path(owned)
        if os.name != "nt": os.chmod(root,0o700)
        db=root/"session.sqlite3"
        cn=connect(db,create=True); cn.close()
        first={"kind":"open","key":"w7-human-open-000001","actor":"human","title":"Synthetic"}
        code,result=subprocess_worker(db,first)
        require(code==0 and result["applied"],"OPEN_NOT_COMMITTED")
        sid=result["session"]
        code,replay=subprocess_worker(db,first)
        require(code==0 and replay["replay"] and replay["session"]==sid,"DURABLE_SUCCESS_REPLAY")
        passed("committed-session-and-success-retry")

        # 100 deterministic crash/restart comparisons. Each stage is tested
        # through an actual terminated Python process and a new recovery read.
        for i in range(MAX_REPS):
            snapshot=inspect(db)
            current=next(x for x in snapshot["sessions"] if x[0]==sid)[2]
            stage=("before_begin","after_write_before_commit","after_commit_before_ack")[i%3]
            op={"kind":"change","key":f"w7-crash-{i:04d}-000000","actor":"agent",
                "session":sid,"expected":current,"title":f"step-{i:04d}"}
            code,_=subprocess_worker(db,op,stage)
            require(code=={"before_begin":82,"after_write_before_commit":83,
                           "after_commit_before_ack":84}[stage],"FAULT_NOT_TRIGGERED")
            recovered=inspect(db)
            observed=next(x for x in recovered["sessions"] if x[0]==sid)[2]
            expected=current+int(stage=="after_commit_before_ack")
            require(observed==expected,"RECOVERY_VERSION_MISMATCH")
            require(len(recovered["events"])==expected+1,"RECOVERY_EVENT_MISMATCH")
            code,resumed=subprocess_worker(db,op)
            require(code==0,"RECOVERY_RETRY_FAILED")
            if stage=="after_commit_before_ack":
                require(resumed["replay"] and resumed["version"]==expected,
                        "COMMITTED_LOST_ACK_WAS_DUPLICATED")
            else:
                require(resumed["applied"] and not resumed["replay"] and
                        resumed["version"]==current+1,
                        "UNCOMMITTED_FAULT_WAS_VISIBLE")
        passed("100-real-crash-restart-faults",{"repetitions":MAX_REPS,
            "faults":["before_begin","after_write_before_commit","after_commit_before_ack"]})

        # Rejection must be persisted too, independent of later session changes.
        rejected={"kind":"change","key":"w7-old-version-000001","actor":"automation",
                  "session":sid,"expected":0,"title":"Rejected"}
        code,reject=subprocess_worker(db,rejected)
        require(code==0 and reject["status"]=="VERSION_CONFLICT","STALE_EDIT_NOT_REJECTED")
        code,replayed=subprocess_worker(db,rejected)
        require(code==0 and replayed["replay"] and
                replayed["status"]=="VERSION_CONFLICT","REJECTED_OUTCOME_WAS_LOST")
        modified=dict(rejected,title="Different")
        code,reuse=subprocess_worker(db,modified)
        require(code==0 and reuse["status"]=="IDEMPOTENCY_KEY_PAYLOAD_CONFLICT",
                "REUSED_KEY_WITH_CHANGED_PAYLOAD")
        passed("durable-rejected-outcome-and-conflicting-retry")

        second={"kind":"open","key":"w7-second-open-000001","actor":"automation","title":"Other"}
        code,r2=subprocess_worker(db,second)
        require(code==0 and r2["session"]!=sid,"SESSION_ID_REUSED")
        require(len(inspect(db)["sessions"])==2,"SECOND_SESSION_LOST")
        passed("independent-simultaneous-session-recovery")

        # A transaction held by a second host must not admit concurrent
        # uncoordinated writers. Simulate a locked writer in another process.
        locker=subprocess.Popen([sys.executable,__file__,"lock",str(db)],
            stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        try:
            require(locker.stdout.readline().strip()=="LOCK_HELD","LOCK_NOT_ACQUIRED")
            locked={"kind":"change","key":"w7-lock-attempt-00001","actor":"agent",
                    "session":sid,"expected":101,"title":"lock-test"}
            code,_=subprocess_worker(db,locked)
            require(code==91,"SECOND_WRITER_NOT_FAIL_CLOSED")
        finally:
            locker.kill();locker.communicate(timeout=5)
        code,afterLock=subprocess_worker(db,locked)
        require(code==0 and afterLock["status"] in ("APPLIED","VERSION_CONFLICT"),
                "STORAGE_UNAVAILABLE_AFTER_LOCK_RELEASE")
        passed("concurrent-writer-lock-fails-closed")

        # Schema and corrupt database must not trigger implicit new empty store.
        versionDb=root/"version.sqlite3"
        cn=connect(versionDb,create=True)
        cn.execute("PRAGMA user_version=8")
        cn.close()
        code,_=subprocess_worker(versionDb,first)
        require(code==90,"UNKNOWN_SCHEMA_ACCEPTED")
        passed("unknown-schema-fails-closed")
        corruptDb=root/"corrupt.sqlite3"
        cn=connect(corruptDb,create=True);cn.close()
        with open(corruptDb,"r+b") as stream:
            stream.seek(0);stream.write(b"CORRUPT!") ; stream.flush();os.fsync(stream.fileno())
        code,_=subprocess_worker(corruptDb,first)
        require(code==90,"CORRUPTED_DATABASE_RESET_OR_ACCEPTED")
        passed("corrupt-database-fails-closed")

        state=inspect(db)
        seqs=[e[0] for e in state["events"]]
        require(seqs==list(range(1,len(seqs)+1)),"EVENT_SEQUENCE_NOT_MONOTONIC")
        passed("monotonic-event-sequence")
    print(canonical({
        "schema":SCHEMA,"seed":SEED,"checks":outcomes,
        "verified":len(outcomes),"sqlite_full_sync":True,
        "w6_host_integrated":False,"operator_host_modified":False,
        "private_source_used":False,"chatgpt_message_sent":False,
        "actor_identity_authenticated":False,"durable_admission_granted":False
    }))

if __name__=="__main__":
    try:
        if len(sys.argv)>1 and sys.argv[1]=="worker":worker(sys.argv[2:])
        elif len(sys.argv)>1 and sys.argv[1]=="lock":
            cn=connect(sys.argv[2])
            cn.execute("BEGIN IMMEDIATE")
            print("LOCK_HELD",flush=True)
            time.sleep(3)
            cn.execute("ROLLBACK")
            cn.close()
        elif len(sys.argv)==2 and sys.argv[1]=="selftest":selftest()
        else: raise RuntimeError("UNSUPPORTED_W7_OPERATION")
    except (RuntimeError,sqlite3.DatabaseError,OSError,ValueError) as exc:
        # Never print payloads or potentially secret DB paths in a log.
        print(type(exc).__name__,file=sys.stderr)
        sys.exit(91 if isinstance(exc,sqlite3.OperationalError) and "locked" in str(exc).lower()
                 else 90)
