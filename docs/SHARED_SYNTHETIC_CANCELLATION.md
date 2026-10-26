# Shared synthetic cancellation composition — issue #51

This isolated Windows workbench connects the durable owned-cancellation core to accessible GUI,
automation JSON and agent JSON-RPC. It is a disposable qualification of shared semantics, not a
production host, verified human identity, real provider action or first-party browser control.
It has no browser, profile, credential or provider target. The production MainWindow is unchanged.

## Authority and actor paths

The workbench uses the same W6 ledger and W9 SQLite checkpoint for sessions, review plans, action
stages and revocation. The live registry enrolls exact Synthetic leases. GUI state and wire replies
are projections of ledger results, never another mutation or replay authority.

The existing experimental issuer adds an explicit `stream.cancel.synthetic` scope. Metadata-only
read/mutate grants cannot cancel. This is not a production cancellation grant. The host checks the
actual enrolled lease surface as well as its session before invoking the shared core. Signed grants
and signing keys remain in the parent. Only non-authority session/target/plan references cross pipes.

The GUI uses an explicitly labeled synthetic human-role fixture, not an authenticated human claim.
CLI and agent children are separately enrolled using the known process ID/start time and observed
Windows pipe peer. The host reobserves the peer inside action admission. Actor/audience/grant come
from that enrollment, never caller JSON. Request parsing rejects unknown fields, unsupported methods,
invalid references and malformed action keys. JSON-RPC IDs and result/error envelopes are checked.

The common core preserves current scope, plan, session/target CAS, immutable outcomes and write-ahead
REQUESTED ordering. Local-signal verification remains distinct from callback completion, transport
completion or provider termination. Both provider and callback verification remain false.

## Qualification

The UIA harness requires named semantic controls and patterns. Confirmation is initially disabled;
Preview enables it without an effect; Confirm applies the reviewed local signal. Two actual children
then exercise automation JSON and agent JSON-RPC concurrently through the same core. They require
immutable replay, cross-session refusal and rejection of an attempted caller-supplied actor field.
A separately enrolled metadata-only child is refused, and a new agent child cannot replay after its
grant is revoked in the ledger. Public receipts export fixed booleans/counts, not role IDs, grants,
OS identities, checkpoint content, private paths or GUI JSON projections.

Recovery reopens the actual SQLite checkpoint into a new ledger instance. Historical nonrevoked
results agree with the GUI projections; agent revocation persists. Fresh replacement leases stay
uncancelled when old actions are replayed. Worker tasks are cancelled/drained before authority is
disposed. This workbench does not itself exercise uncertain-effect crashes; the separate six-case
durable SQLite qualification remains required for those windows.

Run `tools/local/Test-SharedCancellationLab.ps1` with a fresh receipt path after building the
PeerLedgerRdte project. The app mode is `--shared-cancellation-receipt`; child modes are research-only.
No private Actions, Docker, existing credentials or authenticated profiles are involved.

## Remaining production gates

Production issuer/host enrollment and custody, actual cross-account/hostile-path/rollback tests,
human admission, and production GUI/CLI/app-server adoption are not established by these fixtures.
Do not issue this research scope for real Responses/Codex work or generalize it to first-party
traffic modification. Broader authority and eventual merge/release remain HiC decisions.
