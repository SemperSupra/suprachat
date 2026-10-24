# W12 T3: synthetic stream-control boundaries

This experimental tranche stacks on the unmerged owned-transport trace PR #55
and traces back to issue #51. It extends T0/T2 with **fixture-only** stream
control tests, not live browser or provider interception.

## Boundaries

The synthetic simulator and fixture trace source live exclusively in the contract-test project; the shipped core contains only the fail-closed classification helper. No fixture actuator is packaged into the desktop, CLI, or agent runtime.

TraceControlBoundary classifies the existing metadata operation vocabulary
without making an enum, untrusted role label or URL into an actor credential.
Every provider actuation operation remains refused. Starting and stopping
metadata capture still requires the existing process-local session capability.

SyntheticFrameSimulator accepts only opaque, locally minted fixture objects,
a UUID operation key, an integer frame count and a closed treatment enum.
It has no browser handle, HTTP client, URL, socket, payload, account or
external-process connection. It deterministically models dropping a final
frame, repeating it or failing before delivery. It cannot modify a real
stream. All receipts are explicitly SYNTHETIC_ONLY and never claim verified
provider effects.

Each fixture has a bounded first-outcome table. Exact operation retries return
the original immutable receipt. Changed arguments with a reused operation
key are conflicts, not new executions. Foreign, closed, and disposed fixtures
are refused. Limits constrain fixture count, result count, and frame count.

## Qualification and missing evidence

The existing contract runner invokes SyntheticFrameControlChecks.Run in its
normal Windows/Linux/macOS CI matrix. Negative tests cover unrecognized
operations, attempted provider authority, first-outcome conflicts,
cross-fixture access, close/reuse, capacity exhaustion and disposal.
No credentials, private source, real user traffic or Docker are required.

This branch is not a W8 authentication implementation, live traffic actuator,
durable store or remote agent control endpoint. The next dependent milestone
remains an independently qualified actor/session grant and safe cancellation
semantics for SupraChat-owned transports. Native WebView2 completion is
separately unresolved in PR #53. Do not merge or deploy from synthetic CI
alone without independent source/privacy/security review.
