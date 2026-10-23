# W13 — durable bearer self-revocation, one W6 authority

**Status:** isolated synthetic metadata-only RDTE, not authenticated user enrollment
or production authority. W13 builds on W12 signed W6 host writes and W11's
owner-only WAL snapshot, preserving the same W6 mutation engine.

## Correction

Prior W8/W12 HMAC admission demonstrated session-scoped permissions, but
revocation was process-local or absent: a killed host could resurrect a
previously revoked bearer. W13 adds one narrow capability to the *existing*
SharedSessionLedger checkpoint schema:

- `RevokedGrantIds` optional ordered array, capped by W6's bounded receipt
  capacity and validated as unique canonical GUID-N identifiers on restart.
  Older W11 snapshots remain readable with no revocation array.
- `RevokeOwnGrant(grantId,sessionId,audience)` is called **only after**
  host validation of the very same signed bearer token. It adds the grant ID
  to the existing W6 durable snapshot (with a legible metadata event for
  an existing session) before ACK. A repeated self-revoke is idempotent
  `ALREADY_REVOKED`.
- `Open` and `Change` accept an optional W12 signed grant ID. Both check
  the durable revoked set **inside the same ledger lock** as mutation and
  compare-and-swap. A concurrent revocation cannot complete and then permit
  a revoked-grant write or replay via an earlier middleware check.
- The real loopback host supports
  `POST /v1/grants/revoke-self` in opt-in W12 synthetic mode only.
  A client presents its *own signed grant* using the same actor-grant header
  and passes its signed target session; the body cannot specify an arbitrary
  target grant ID. A forged audience or another session fails closed.
  Existing machine CLI `session-ledger-revoke-own` and JSON-RPC
  `session/ledger/revoke-own` reuse the same W6 client. The old default
  W6 mode does not expose a mutation-capable revocation endpoint.
- A revoked bearer cannot exercise its former scope even after a host
  restart. A separately signed new short-lived grant for the **same**
  actor can retry the original operation key and obtain the immutable
  historical first outcome. Revocation does not silently rewrite that
  receipt or force a new metadata mutation.

## Test evidence / validation expectation

The real W12 three-audience / two-session process test is extended to:
forged role on revoke denied; signed self-revoke committed; same grant
mutation subsequently forbidden; repeat revoke returns ALREADY_REVOKED;
parallel mutation-vs-revocation race either applies before revoke or is
denied; subsequent use always fails; kill and restart actual W11 W6 host;
revoked grants remain refused and a fresh signed grant can replay the
actor's original immutable outcome. The same accessible desktop observer
still sees both sessions and a legible session metadata event.
Windows/Linux/macOS public-free GitHub Actions run this test alongside
the older W11/W12 regression and default W6 ephemeral tests.

## Hard remaining gates

- This proves **durable self-revocation only**, not owner-initiated
  revocation of a different actor. Cross-actor owner approval and
  user-facing enrollment require a verifiable local principal, safe issuer,
  key custody/rotation, trusted owner operation and session-wide policy.
- A stolen signed capability remains a bearer until its own holder
  revokes it or it expires. The host does not distinguish a real human,
  OS-token subject, process, device or MFA assertion. No HIL on a real
  ChatGPT account is authorized by these fixtures.
- W11 snapshots are still bounded/unencrypted synthetic data. Windows
  ACLs, malicious same-user access, storage wear, actual power loss,
  ENOSPC/torn sync, independent storage audit, and retention/migrations
  are outstanding. Do not assume this is production revocation storage.
- The cross-process metadata host is not the authoritative GUI session
  model yet: accessible desktop's current shared view is **read-only,
  unbound** and cannot submit ChatGPT prompts or terminal commands.
- Keep all draft/merge/release/credential/provider/private-source gates.
