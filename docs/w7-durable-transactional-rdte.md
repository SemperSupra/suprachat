# W7 durable shared-session recovery — isolated synthetic RDTE

This is a bounded source and causal-evidence experiment for
[SupraChat #38](https://github.com/SemperSupra/suprachat/issues/38).
It is stacked on W6 [draft #37](https://github.com/SemperSupra/suprachat/pull/37)
at exact source `957fddff82763fb2ea8eeac1837dfea6ce4ff431`,
but does **not** replace or modify W6's deliberately ephemeral loopback host.
Any reader must not infer that `session-ledger-host` has become durable.

## Deliberate DB design and first-outcome guarantee

- Experimental .NET 10 executable with Microsoft.Data.Sqlite 10.0.0.
- Single explicitly launched owner process using an exclusive `.ownerlock`;
  SQLite WAL with `synchronous=FULL`, transactional DDL, transactional
  metadata and idempotency outcomes, `BEGIN IMMEDIATE` writer exclusion.
- One SQLite transaction advances a global monotonically increasing sequence,
  applies a version-fenced metadata change (or records the rejection), inserts
  an event, persists the immutable first-response JSON and advances metadata.
  **No APPLIED or REJECTED response before transaction commit.**
- A repeated operation key and identical normalized operation fingerprint
  returns the *original result* with `IsReplay=true`; changed payload under
  the same key is denied without a new mutation. An old `VERSION_CONFLICT`
  remains an old conflict on retry, even if a later write changes the session.
- Reads never expose uncommitted writes and events. On a failed SQLite
  operation the owner process exits with code 78; it does not reset to an
  empty store or send a synthetic success.
- Intentional crash hooks: `BEFORE_COMMIT` exits 88 without committing,
  `AFTER_COMMIT_BEFORE_REPLY` exits 89 after durable transaction completion
  but before sending a response. This is no simulated provider request.
- Host restart validates schema version, SQLite `quick_check` and
  `integrity_check`; unsupported schema/corruption or second owner fails
  closed. The test includes **100 deliberate crash/restart iterations**,
  including 50 before and 50 after the commit boundary, and checks retained
  successes, rejections, ids, ordering and replay.

## Known limitations and independent review gates

- W7 does not yet integrate with W6, use a UI, call ChatGPT, execute terminal
  commands, grant delegated identity, encrypt metadata, replicate to DLE, or
  make a production storage claim. No real user data or credentials belong in
  these fixtures. Audience strings are still **unauthenticated labels**.
- SQLite WAL FULL sync provides an established local ACID primitive, but the
  experiment has not independently established crash behavior for every
  filesystem, controller/cache barrier, VM, SSD, malicious filesystem or
  unrecoverable power-loss condition. Forced process exit is a **power-loss
  analogue**, not actual power loss. The test also does not inject real ENOSPC
  or mid-fsync torn writes. The WAL and SHM files may exist alongside the DB.
- Creating a store in an uncontrolled, writable directory is unsafe against
  same-user hostile processes and symlink/subdirectory races. Owner-only
  permission and path custody, encrypted sensitive fields, backup/restore,
  retention, schema migration, disk-full fault injection, tamper-resistance,
  and OS-hard actor separation require separate design and tests.
- No network endpoint is exposed by this experiment; line-oriented stdio
  is for the harness only. Do not use for untrusted code or general clients.
- First-outcome replay is scoped to metadata-only operations, not exactly-once
  ChatGPT provider submissions. A real remote uncertain submission must remain
  quarantined and reconciled independently.

## Reproduce with disposable metadata

```sh
dotnet build SupraChat.DurableRdte/SupraChat.DurableRdte.csproj -c Release
python tools/qualify-w7-durable-ledger.py SupraChat.DurableRdte/bin/Release/net10.0/SupraChat.DurableRdte.dll
```

The test creates its own disposable local stores and synthetic labels.
Three-platform GitHub Actions uses **public** runners and no Docker,
private checkout, authenticated provider, or real external service.

## Sequential integration conditions

1. Exact-source three-platform tests, fault receipts, independent persistence
   and security review; no masked failures.
2. Decide the W6 in-memory ledger transition boundary: persist **each immutable
   first response** in one transaction with session metadata and event history,
   or replace the W6 ledger with the admitted durable implementation; never
   run two competing mutation authorities.
3. Preserve W8 participant auth as a separate gate. Introduce actor/session
   permission binding before access beyond one trusted local OS user.
4. Requalify real W6 CLI/HTTP/JSON-RPC three-audience simultaneous session
   tests across deliberate restart and error injections, plus GUI accessibility
   HIL; only then mark `durable_recovery_available=true`.
5. Owner approval for final migration, local path/permissions, and deployment.
