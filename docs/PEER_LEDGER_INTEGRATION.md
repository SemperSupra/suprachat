# Enrolled peer / durable session / trace integration — issue #51

This candidate stacks on #60 and composes one selected W6 ledger and W9 checkpoint with
actual Windows pipe peer observation. It remains a disposable metadata fixture. No desktop
ChatGPT window, credential provider, human grant, live stream cancellation or remote issuer is wired.

## Source selection and authority

The public source baseline is #52, `b1ecb53c76b369044725a0e16f582e9c26dfcb7a`, which incorporates
the #49 W11 checkpoint reconciliation and #52 durable self-revocation:

- `SharedSessionLedger.cs` and `ChatGptControlContract.cs` are reused in the shared product core;
  only the ledger declaration becomes partial to add the trusted-host admission entry point.
- `W9DurableSessionStore.cs` and `W12ScopedGrantAuthority.cs` are reused inside the standalone
  research executable. W9 gains one explicit disposable fault: SQLite query-only mode forces
  a real write exception through its existing fail-stop catch.
- The legacy bearer HTTP host and its optional/unscoped admission are **not** imported.

Full-source reconciliation corrects an earlier planning limitation: the actual W9 Save hook already
calls `Environment.Exit(78)` on uncertain serialization/write/commit. The ledger alone does not
roll back advanced memory; the selected hook supplies process termination under that same gate.
Explicit precommit/postcommit exits are 88/89. Neither graceful restart nor a poisoned-ledger
exception is substituted for that requirement.

## Peer and grant boundary

The fixture parent launches and enrolls each child instance with a fixed actor, audience, session
and read/mutation scope. A fresh CurrentUserOnly pipe admits one bounded exchange. The server
reads a length-bounded frame, derives the actual PID, compares the enrolled process creation time,
and observes the client's impersonation token SID. Actual OS values are kept in memory only.
The peer is checked again inside admission. An unenrolled case deliberately mismatches the
expected OS process; a forged human audience is refused rather than becoming human approval.

The issuer's random signing key and grants stay in the parent. They are not placed in arguments,
environment, pipe messages, logs or receipts. Child environments are cleared except Windows
runtime/temp paths. A rendezvous name or session ID does not grant authority. This qualifies a
parent-enrolled child fixture, not arbitrary executable trust or production issuer custody.

`ExecuteAdmitted` runs the trusted host's peer/grant verification under the original W6 `_gate`.
It rejects missing or human admission, checks durable revocation before reads/replay/mutation,
and namespaces idempotency by verified actor. Existing Open/Change/RevokeOwnGrant perform all
mutations and checkpointing. A new grant for the same actor can replay its historical first
outcome; a revoked grant cannot retrieve it. Admission refusals occur before operation execution
and are not represented as durable provider-action outcomes.
Repeated self-revocation with the revoked grant also returns GRANT_REVOKED; it does not bypass
admission to manufacture a new success result.

`InspectProjectionAdmitted` copies the bounded sanitized trace page inside that same gate.
Revocation cannot commit between authorization and the copy. Callbacks are trusted synchronous
host code: no external I/O, asynchronous work or inverse trace-to-ledger lock acquisition is allowed.
The parent alone holds trace capabilities. `TraceNormalizer.CreateSession(Guid)` uses the ledger's
session UUID while preserving capability-reference checks, duplicate-ID refusal and resource limits.
Knowing the UUID still cannot read a trace. Trace failure/storage never changes ledger revisions.

## Qualification

Seventeen real-process cases cover metadata success/replay/conflict/CAS, wrong session, forged
human, insufficient scope, actual enrollment mismatch, a second agent/session, trace read/isolation,
self-revocation, revoked replay, expired grant and three checkpoint crash/restart cases.
The write-failure case triggers an actual SQLite read-only write error and requires exit78.
After restart, before-commit/write-error attempts apply once; postcommit-before-ack retries replay
the saved first result without a second revision. Revocations and first outcomes recover together.

Cross-platform contracts coordinate a trace projection and concurrent revocation, then prove the
projection completes before revocation and a subsequent read callback is never invoked. They
also prove actor-scoped replay, durable revocation precedence and UUID-without-capability denial.

```powershell
dotnet run --project SupraChat.ContractTests -c Release
dotnet run --project SupraChat.PeerLedgerRdte -c Release -- --receipt "$env:TEMP/fresh-peer-ledger-receipt.json"
```

Receipts export closed statuses/booleans only. SQLite files stay in fresh local disposable directories;
only the sanitized receipt enters public/free CI. The first incomplete local run is retained: its
incorrect test expected a new empty trace to assert a gap. A restarted trace instead reports no
recovered events and completeness UNKNOWN; the fixture makes no durable trace-recovery claim.

## Remaining gates

Windows directories inherit ACLs; custody and cross-user denial are not qualified. The private issuer
is ephemeral, not a persisted production trust root. Human approval, hostile process races, remote
delegation, key rotation, privileged export, shared provider-control effect receipts and actual desktop
observer/CLI/agent host composition need separate qualification. Existing local control surfaces remain
unchanged. No second session mutation owner, live capture or production authorization is introduced.
The per-session first-party consent flow and explicit operator opt-in still precede signed-in HIL.
Independent source/security review and exact-head CI precede acceptance; merge/release is reserved.
