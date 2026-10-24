# W12 integration review and next admission

This is a source-based selection for the next **synthetic integration candidate**, not a production
promotion, PR merge or authorization to use real ChatGPT traffic. Current main and all experimental
candidate heads must be reconciled again before composition.

## Selected authority shape

Advance W6 SharedSessionLedger with the W9 transactional backing checkpoint and W10 read-only
accessible observer, as hardened by #49/W11. Retain one existing W6 mutation gate and one checkpoint
authority. The reviewed #49 source persists a checkpoint under that same gate before returning the
result to a caller; the checkpoint includes bounded session/event/first-outcome state. In-memory
changes precede that persistence callback, so a failed write requires the actual host to fail-stop.
This does not establish in-memory rollback or safe reuse of a ledger after an uncertain write.
An observer may read a projection but must not create a second mutation implementation.

The separate direct-SQL #46/#48 lineage stays comparative research. It is not composed into another
live authority alongside W6. Its incremental row-write approach may become relevant if measured
checkpoint latency or bounded capacity becomes a problem, after its own qualification.

The trace normalizer remains a separate bounded observation buffer. Recorder failure never advances,
rolls back, creates or recovers an authoritative session revision. The T3 local cancellation registry is
also not a durable session ledger: its local grants and action outcomes expire with the owning process.

## Identity source and blocking proof

#44 proves a synthetic signed-grant primitive. #50 adds scoped-write admission to the actual synthetic
W6 host; #52 persists self-revocation through that same checkpoint/gate. These are useful components,
but possession of a signed bearer is not proof of an OS user or human. Their own source descriptions
explicitly leave trusted issuer enrollment, production signing-key custody, actual peer binding and
Windows ACL admission unqualified. A shared transport bearer must never become a write bypass.

Before privileged shared-actor control/export, qualify a trusted owner/issuer and bind delegation to an
observed local peer. On Windows, a bounded named-pipe peer-identity proving rep should establish the
actual client process/OS subject and enforce owner-only custody; process identity and human approval
must remain distinct claims. Signed actor/audience/session/action/expiry and durable revocation must
be checked inside the same W6 mutation gate, including immutable replay. No actor-provided role,
trace label, environment variable or browser session is sufficient to assert that identity.

## Sequential integration gates

1. Qualify #59 local control semantics and the native shared-trace adapter at exact heads, alongside
   #53's independent oracle. Preserve genuine failures and gap semantics.
2. Independent review of source, security, privacy, accessible controls and performance. The current
   author-run source checks are not labeled independent acceptance.
3. Qualify owner/issuer enrollment, Windows custody and actual local peer binding before allowing
   privileged shared-actor operations. Preserve the #52 no-bypass revocation and first-outcome rules.
4. Compose trace adapters with the selected W6 session IDs/revisions and event cursor through one
   semantic core. A trace or local control UUID must not silently stand in for an authoritative W6 session.
5. Prepare an explicit, per-session, metadata-only first-party consent flow and then obtain operator
   opt-in for signed-in Windows HIL. Existing signed-in state stays local; no cookies or renewable
   authority cross into trace, CI, issue text, actor prompts or remote clients.
6. Only after acceptance consider owner-authorized merge/release. Encrypted payload storage, real
   first-party modification and macOS/Linux native adapters remain separately qualified later packets.

References: issue #51; PRs #44, #49, #50 and #52. None is treated as merged or production-qualified.
