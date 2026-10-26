# Native consent composition review — issue #51

Compared PR67 at `04262c79c9d98356f3a502c5d2763e851708c408` with the PR71–74 lineage at
`1667c2801061aa79106bc0dd7a55d8fc8f7150af`. Their common base is PR65
`0e29f14139e4fe0a4fb73b1fae6fd9f84dc086d9`. This is an integration proposal and regression
reconciliation, not a merge or closure decision.

| Invariant | PR67 | PR71–74 composition | Decision |
| --- | --- | --- | --- |
| OFF, acknowledgement, bounded review/lease | Implemented | Preserved; typed phrase removed on operator instruction | Retain current UI and admission |
| Producer isolation | Public synthetic-native binding | Internal source-specific binding, exact recorder/view/plan sink | Preserve plan-bound sink |
| Old producer after replacement | One-shot revocation followed by asynchronous detach | Old plan immediately fails the sink fence | Preserve current plan fence |
| Idle expiry | Headless timer test reads raw recorder state | Timer implementation existed, but status reads could trigger expiry in native test | Port independent-observation test intent |
| Retired timer callback | Forced disposed-timer regression | Existing fake suppressed disposed callbacks | Port forced callback regression |
| Pending enable / reentrant Stop | Basic enable/disable ownership | Registration-publication barrier and pending-owner retention | Preserve current ownership machinery |
| Raw JSON privacy | Callback gate after SDK materialization | Admission before and after SDK JSON getter | Preserve current boundary |
| First-party route | Always unavailable | Explicit local opt-in, exact-origin retention, aggregate-only save | Keep scope separately qualified |
| Evidence | Basic stopped/loss status | Closed incident reasons, first ending and enable/disable/cleanup flags | Preserve current evidence semantics |

## Reconciled test intent

The revised actual WebView2 fixture pauses only its synthetic controller's UI watchdog. An internal,
synthetic-only observation reads the recorder's existing mode and adapter stopped flag without
calling consent refresh, Status, Read, Core.Source or browser COM. It does not mutate either state.
Neither the observation nor watchdog control is available to the first-party route.

Using a virtual monotonic TimeProvider, the fixture stops and replaces a lease at119 seconds, forces
its disposed timer immediately and again after the original deadline, and verifies the replacement
survives. An early current callback must reschedule its own timer. The replacement stays active at
its119-second point and the timer switches the recorder OFF at120 seconds before the UI thread
yields. Native detachment is then observed without status polling, watchdog activity or new fixture
requests. Ordinary summary/status APIs run only after these independent assertions.

This tests virtual-time lease ordering plus actual native cleanup. It does not replace real-time,
operator-authorized first-party expiry HIL, establish complete browser delivery or verify provider
termination. Existing native race, navigation, review UI and privacy checks remain required.

## Proposed integration order

Use the existing composition's base relationships: PR65 →71 →72 →73 →74, followed by this bounded
test reconciliation. Their earlier parent chain remains54 →55 →59 →60 →62 →63 →64 →65. Independent
feasibility/peer experiments and PR67 supply evidence; they are not automatic extra merge parents.
Compare any desired unique delta and transplant its intent into the chosen authority rather than
introducing a second consent or session authority.

Do not merge PR67 wholesale into the composition: overlapping gateway and adapter files implement
different lifetime APIs. Do not close or declare another actor's draft superseded without the owner
decision. Integration still requires exact-head qualification and HiC merge/release authorization.
