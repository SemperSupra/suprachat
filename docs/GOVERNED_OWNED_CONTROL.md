# Governed owned-stream control — issue #51, T3

This candidate stacks on #55. It implements a single-operator process-local control experiment,
not W8 peer authentication, durable session authority or first-party ChatGPT actuation.

## Control semantics

LocalOwnedStreamControls registers at most 16 active owned targets. Responses HTTP/SSE requests,
Responses WebSocket connections and known Codex turns receive separate random local stream IDs.
Trace aliases correlate those IDs without exposing provider identifiers. The target set is independent
of whether metadata capture is ON or OFF; starting capture never starts a provider operation.

Cancellation requires a preview for one target, its exact revision and a fresh action key. The preview
does not apply any effect. Explicit confirmation applies that exact plan, which expires after two
minutes. Wrong sessions, missing plans, stale revisions, changed action-key bindings, expired plans
and unavailable targets are refused. At most 64 unexpired plans and 128 first outcomes are retained;
capacity exhaustion refuses new work rather than evicting replay evidence. State is process-local,
not durable across restart.

The first result of an action key is immutable, including refusal results. Exact retries return that
same receipt even after the target finishes. Changed bindings conflict. Concurrent exact duplicate
cancellation signals the same target once. Completion/removal and effect application share the
target registry's gate. CTS.CancelAsync queues callbacks; provider or user callback code is not run
synchronously under the mutation gate. Callback completion is not claimed in a receipt, and callback
errors are observed without exporting their messages. Lease cleanup waits at most two seconds for
callbacks; controller disposal does not itself authorize cancellation of provider work.

An APPLIED cancellation receipt verifies only that the local cancellation token was signalled.
ProviderStopVerified and CallbackCompletionVerified remain false. HTTP and WebSocket boundaries
consume the local token. Codex registers known turn IDs from successful turn/start responses before
publishing the response to callers, so a following turn/completed frame cannot leave an orphan target.
Cancellation sends one bounded supported turn/interrupt RPC; a missing acknowledgment, race with
natural completion or local cancellation must not be called proof of remote compute termination.
No request is automatically retried.

## Surfaces

Desktop: refresh active owned streams, select one, preview cancellation, then confirm. Changing the
selection invalidates the pending UI confirmation. No target means confirmation stays disabled.
These controls sit beside the metadata controls and expose stable semantic IDs and a polite status.

Agent stdio methods: trace/streams, control/preview and control/apply. The same operations are
available in the persistent CLI trace-session loop. Preview params are stream_id, action=cancel,
expected_revision and action_key; apply params are plan_id, expected_revision, action_key and
confirmed=true. Optional session_id must match the current process. Unsupported authority claims
are refused by the common controller. No machine field can assert a trusted human/OS identity.

Long-running Responses commands do not block later control commands: stdio permits up to eight
in-flight Responses calls while processing control messages. The CLI command loop issues an
accepted command_id for a provider operation and later returns its result/error with that command_id;
up to eight operations are active. It continues to process metadata/control commands meanwhile.
JSON-RPC replies keep their caller IDs and may arrive out of order. Existing stdout serialization
preserves one JSON object per line. No other agent RPC family is made concurrent by this change.

## Synthetic fault matrix and denied operations

The same approval, revision and first-outcome rules govern delay/drop/fault/replay on targets created
explicitly as Synthetic. Their fixture produces only four fixed synthetic strings, a bounded 20ms delay,
one dropped frame, one repeated frame or a fixed fault. There is no URL, provider callback, arbitrary
payload or wire-send parameter. A synthetic action on a real owned surface is refused. First-party
modification and payload capture are always refused, even with confirmation. Production shells do
not expose synthetic target creation.

## Qualification and remaining gates

The contract executable tests preview-without-effect, confirmation, wrong sessions, expiry, stale CAS,
immutable successes/refusals, conflicting keys, ten concurrent duplicate cancellations, independent
targets, resource limits, wire projections, synthetic effects, real token cancellation of fake HTTP and
WebSocket transports, and a persistent synthetic Codex subprocess exercising turn/interrupt. No real
credentials or provider inference are used. The machine helper checks equivalent CLI/agent denials;
Windows UIA checks all control peers and verifies no-target confirmation remains disabled.

Local build, full contract executable and machine helper passed. Public tri-OS qualification is required
for the exact candidate head before acceptance. Independent review remains required before merge.
T4 shared-actor actuation is blocked on actual peer identity and the selected session authority; this
local registry is not a substitute. Browser modification, signed-in capture, durable action replay,
encrypted payload storage and release admission are not enabled by this candidate.

First public Windows rep 40060467458 passed transport/core/machine tests but hit E_UNEXPECTED in
UIA FindFirst during the live accessibility step; Linux and macOS passed. Semantic lookups now log
the exact public control ID and allow a bounded three-second readiness window only for missing
elements or that specific provider-transition HRESULT. A persistent failure or missing accessible
name still fails; successful receipts record the transient retry count. This is a qualification change,
not evidence that the original UIA cause has been established.
