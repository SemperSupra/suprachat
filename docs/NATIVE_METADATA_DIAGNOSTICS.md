# Native capture corrective diagnostics — issue #51

Stacked on PR71. The first operator-authorized observation exposed an ambiguity: `native_loss`
counted diagnostic incidents from parsing, callback, attachment and teardown paths, rather than a
measured number of lost network events. Historical summaries cannot recover the cause of an
incident because raw traffic was deliberately not retained. Do not reinterpret old receipts as
lossless capture or assign their incidents to a guessed cause.

A second, independent behavior explains why a saved summary always discloses a stopped interval:
Save stops capture, and the normalizer marks Metadata→OFF as a pending gap. `capture_gap=true`
after Save does not itself demonstrate an adapter failure. That conservative flag is preserved.

## New diagnostics

The production native parser is now independently exercised with synthetic JSON. It retains the
same bounded correlation and origin filters, emits only allowlisted metadata, and never retains raw
JSON. Fixed enum reason counters distinguish observation incidents from attachment/cleanup incidents.
The compatibility `native_loss` field is their sum, explicitly labeled DIAGNOSTIC_INCIDENTS_NOT_LOST_EVENT_COUNT.
No exception text, raw event name, URL, request identifier, header or payload enters diagnostics.
Oversize raw events are checked before origin filtering and therefore can be off-origin incidents.

Summary schema v2 adds the current lease's first ending reason, stopped-interval disclosure, recorder
eviction count, enable/disable completion, cleanup completion and OFF/observer/attaching flags.
The first reason survives later Stop or Save: an expired or navigated lease stays expired or navigated.
Counts accumulate across the local review session; the ending reason describes its latest lease.
Save revokes consent immediately, then waits for bounded cleanup before taking its aggregate snapshot.
Cleanup timeout remains explicitly pending and is counted once per adapter. A pending previous owner
cannot be discarded or replaced; canonical ownership remains reserved until its cleanup settles.
Closing the review revokes capture; no receipt claims an independently observed window close.

Completeness is always UNKNOWN. Neither zero incidents nor successful cleanup proves all upstream
events were delivered. Local disable completion does not prove that provider work stopped. Sign-in,
conversation binding and human identity remain independently qualified claims.

## Qualification and live handoff

Headless checks reproduce clean stop with zero adapter incidents despite the recorder gap, oversized
raw JSON, missing IDs, malformed metadata, origin revocation, lifecycle failures and privacy. The real
disposable native fixture checks settled cleanup and retention of operator-stop/expiry/navigation
reasons. It continues to test default OFF, concurrent views, pending enable races and view invalidation.

After exact-head source review and public CI, use a fresh local review confirmation for another
signed-in metadata observation. The prior capture's permit cannot restart it. Save a v2 aggregate
after each intended ending (Stop, expiry or navigation); inspect reason counters and OFF/cleanup flags.
The operator must report the window-close result separately if computer-use is unavailable. No
automatic capture, automatic evidence upload, live traffic modification, merge or release is added.
