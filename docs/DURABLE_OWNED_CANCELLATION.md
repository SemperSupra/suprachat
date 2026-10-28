# Durable owned cancellation core — issue #51

This packet adds a host-admitted cancellation path to the existing SharedSessionLedger checkpoint.
It is a bounded core and disposable SQLite qualification, not production host admission or a live
provider-control rollout. ChatGptControlContract remains read-only and disconnected. Native ChatGPT
traffic and conversation URLs are never cancellation targets.

## One decision authority

The trusted host registers an exact process-local owned stream lease with the ledger. Registration
exclusively claims that lease: legacy preview/application, including plans created before enrollment,
cannot change it, and another ledger cannot claim it. Unclaimed streams keep their existing local
workflow. The process-local registry executes effects but stores no competing durable decisions.
Disposed/cancelled targets are pruned on enrollment; at most 64 live registrations and 128 retained
plan/outcome records are allowed. Capacity refuses new plans rather than evicting replay evidence.

Each operation requires fresh host-supplied cancellation-specific admission under the ledger gate.
Metadata mutation permission is insufficient. The admission binds actor, audience, session and grant;
revocation is checked before historical reads as well as mutation. This callback is a trusted host
boundary, not a new issuer or proof of actor authentication. No public caller-supplied JSON can enroll
a live executor. Human, automation and agent audience labels use the same decision functions.

For transport-created leases, the host can enroll by stream ID only while also holding the exact live
owner registry. The ledger resolves and exclusively claims the lease under the same lock order.

Preview binds actor, original grant, action key, authoritative session version, exact target revision,
host-run ID and a two-minute monotonic review lifetime. Confirmation must match the stored plan.
Admitted first confirmation refusals are persisted and immutable. Changed bindings conflict.
A currently authorized renewed grant can read confirmed historical outcomes for the same actor,
but cannot execute an unconfirmed old-grant plan: it must request a fresh review with a new key.

## Crash ordering

Under ledger→stream-registry lock order, confirmation advances the session version and commits
approval plus REQUESTED through the same synchronous W9 persistence hook before signaling the CTS.
It then appends separate APPLIED_LOCAL_SIGNAL and VERIFIED_LOCAL_SIGNAL stages. Verification means
that the exact lease's cancellation token was requested. It does not mean callbacks completed,
transport shutdown completed, a provider accepted cancellation or remote computation stopped.
No provider/user callback is awaited while either decision gate is held.

Exact retries return the saved stages without redispatch. Restarted hosts retain decisions but no
executor capabilities. An unconfirmed old-run plan is refused. A committed REQUESTED whose effect
is unknown remains OUTCOME_UNKNOWN_FRESH_APPROVAL_REQUIRED. An APPLIED stage without a verified
stage remains APPLIED_LOCAL_SIGNAL. Recovery never retries against a recreated or newly enrolled
target. Approving a different target always requires a fresh plan and key.

Old checkpoints load with no control journal. New checkpoints validate bounded records, unique plan
IDs and stage sequences, legal stage prefixes, confirmation consistency and session versions.
Once a control record exists, the checkpoint uses a distinct control/v2 schema. Older ledger hosts
reject that schema rather than loading, silently discarding the journal and overwriting decisions.
Relabeling a control-bearing checkpoint as legacy, or stripping its journal while retaining v2, is refused.
Checkpoint writes must use the existing qualified fail-stop hook: storage uncertainty terminates the
host before any later read can expose uncertain in-memory state. The core also refuses further control
work after a thrown commit. It does not claim arbitrary caller-provided persistence is durable.

## Qualification boundary

Headless synthetic checks cover concurrent duplicates, immutable replay, malformed restore chains,
actor/session/scope/refusal/revocation, expiry/CAS, removed targets, grant renewal, legacy bypass,
exclusive ownership, repeated target cleanup, bounded journal capacity and callback failure. In-memory
fault hooks exercise request/applied commit windows; those checks alone are not SQLite durability proof.
Additional fixtures route durable cancellation through injectable Responses HTTP, WebSocket receive
and blocked-close handlers, and a real child JSON-RPC fixture's supported Codex turn/interrupt path.
These prove local transport integration with synthetic peers, not remote provider termination.

The actual W9 child-process fixture separately uses disposable SQLite databases. It proves successful
signal/recovery, crash before request commit, crash after request commit, crash before applied commit,
crash after applied commit and an actual SQLite write failure (exit78). It exports fixed case outcomes
only; database/checkpoint contents, actor/grant identifiers, private paths and signal marker files stay
local to the disposable fixture. Existing peer/ledger qualification remains a separate regression.

Production issuer/custody, GUI/CLI/app-server enrollment and control approval surfaces, remote-provider
effect verification, cross-account adversarial qualification and authorized integration/release remain
separate gates. Do not route the current production UI through this disposable research store.
