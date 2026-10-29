# SupraChat source archaeology: reusable algorithms, data structures, invariants and composition plan

**Research snapshot:** 2026-10-29. **Classification:** source review and design proposal, not a product acceptance or permission to deploy. **Base:** public SupraChat main commit [a0fa56c943d7](https://github.com/SemperSupra/suprachat/commit/a0fa56c943d73482ac233834b96f31fb591fd385). No secrets, user accounts, provider sessions or private-source excerpts were used in this report.

## 1. Authority, scope and confidence

This is a **docs-only source inventory** in a distinct draft. The accepted main tree incorporates selected work previously proposed in historical #79/#81/#82/#83/#84 and subsequently reconciled by #90/#91/#92; those old PRs are closed, not active admission. [Draft #93](https://github.com/SemperSupra/suprachat/pull/93) independently investigates intermittent Windows UIA child-root disappearance using a disposable placeholder-versus-native-WebView fixture and the pinned WinApp CLI v0.7.1. Do not duplicate its ownership, remove its first-failure evidence, or infer a cause from a green rerun.

Evidence classes used below:
- **S — source verified:** function, type or contract read from a pinned source path. It describes implementation, not successful deployment.
- **R — runtime observed:** the separate pre-existing synthetic qualification was observed to pass/fail; scope remains synthetic-only.
- **H — hypothesis/test opportunity:** plausible risk or optimization needing an independent reproducer before product changes.
- **HOLD:** not admitted or not qualified; no merge, release, first-party authenticated traffic actuation, credential capture or private Actions.

## 2. Cross-project source inventory and prior art

| Project and pinned source | Concrete implementation or algorithm (S) | Primitive to harvest | Limit |
|---|---|---|---|
| **SupraChat current main**, [MetadataCaptureConsent](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/MetadataCaptureConsent.cs#L31-L215) | Dictionary keyed by object-reference bindings and plans, lock-protected state, monotonic TimeProvider review timeout (60 s), lease (120 s), epoch/revision fence, immutable first outcome | Revocable **process-local authority** and guarded sink | Serialized IDs and UI Automation element IDs are not capabilities; source does not grant additional first-party access |
| **SupraChat current main**, [WindowsWebViewTraceAdapter](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/WindowsWebViewTraceAdapter.cs#L59-L135) | Singleton ownership map keyed by COM IUnknown identity, cap 16, before/after native callback materialization checks, attach/disable completion barrier | Exclusive native adapter ownership; revoke-before-reading | COM IUnknown identity is **not** the same as Win32 HWND, UIA RuntimeId or a portable session ID |
| **SupraChat current main**, [NativeMetadataEventProcessor](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/NativeMetadataEventProcessor.cs#L9-L79) | HashSet of up to 256 in-scope request IDs; raw input bound to 1 MiB, request ID 512 chars; origin + scheme + port check; allowlisted metadata event projection, explicit diagnostic gaps | Bounded correlation state machine and data-minimizing projection | No full request/response reconstruction; oversize/off-origin input can cause a diagnostic gap |
| **SupraChat current main**, [TraceNormalizer](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/TrafficTrace.cs#L115-L244) | Lock-protected Dictionary of max 16 sessions, per-session bounded Queue (default 256), monotonic sequence, eviction cursor/counter, pending capture-gap bit, per-process HMAC-SHA256 pseudonyms | Single metadata observation contract and explicit **unknown completeness** | Source timestamps/aliases describe adapter observation, not provider effect; no payload capture |
| **SupraChat current main**, [ReviewOperationFence](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/ReviewOperationFence.cs) and [lifecycle readback](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/NativeMetadataLifecycleReadback.cs#L10-L66) | Generation counter invalidates stale async review completions; bounded 24-entry list and identical-Observed fingerprint suppression; monotonic offsets and closed action/state vocabulary | Operation cancellation fencing; compact non-authoritative state projection | Last 24 events are not a complete audit timeline; history remains explicitly UNKNOWN completeness |
| **SupraChat current main**, [NativeMetadataSummary](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/NativeMetadataSummary.cs#L23-L50) and [SummaryStore](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/NativeMetadataSummaryStore.cs#L16-L54) | Versioned v2-to-v3 aggregate extension and producer-side last-event consistency checks; same-directory unique .pending write, Flush(true), no-overwrite rename | One existing local aggregate with versioned evidence and create-only commit | Not power-loss-proof; a crash may strand .pending. Do not introduce another durable consent ledger |
| **SupraChat current main**, [SharedSessionLedger](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/SharedSessionLedger.cs#L14-L79) and [durable cancellation](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/SharedSessionCancellation.cs) | Bounded maps/journal, monotonic sequence, idempotency replay, expected version/revision, schema-checked restoration; host commit hook must fail-stop before acknowledging accepted outcomes | Existing authoritative semantic session/control state machine | UIA/WinApp observers must consume projections, not register themselves as a parallel session or provider authority |
| **SupraChat current main**, [CredentialBroker](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/CredentialBroker.cs#L172-L241) | Sorted/deduplicated capability arrays, structured JSON fingerprint, cached first lease outcome, separate epoch/generation; [clone-on-return authority array](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/Core/CredentialBroker.cs#L373-L401) | Canonical request identity and defensive DTO snapshots | Do not give UIA element references broker capability; broker wall-clock expiry is not a substitute for monotonic test deadlines |
| **Avalonia 12.1.0**, [TextBoxAutomationPeer](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Controls/Automation/Peers/TextBoxAutomationPeer.cs#L6-L32) | Stock peer implements IValueProvider, returns **Owner.Text** directly, and raises ValueProperty changed with raw old/new Text values | Exact-version security test oracle | Different newer Avalonia main masks displayed text: not applicable to pinned 12.1.0 without upgrade qualification |
| **SupraChat current main**, [SecurePassphraseTextBox](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat/SecurePassphraseTextBox.cs) | Real control sets mask + RevealPassword=false; custom peer removes IValueProvider and suppresses Text property notifications | Narrow production secrecy adapter | Existing filled-value UIA scalar qualification is not proof for every event, clipboard, screen or notification channel |
| **FlaUI 5.0.0**, [UIA3Automation](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.UIA3/UIA3Automation.cs#L124-L133), [event registration](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.UIA3/UIA3FrameworkAutomationElement.cs#L195-L244), [handler](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.UIA3/EventHandlers/UIA3PropertyChangedEventHandler.cs#L13-L25) | Native ElementFromHandle(IntPtr) COM bridge; property ID allowlist; callback is handed **raw newValue**; remove handler supported | Pinned, independent UIA3 event observer for *synthetic tests* | Avoid passing raw callback value into logs/receipts; event registration and disposal must both be verified |
| **WinApp CLI v0.7.1**, [UI query implementation](https://github.com/microsoft/winappCli/blob/v0.7.1/src/winapp-CLI/WinApp.UIAutomation/Services/UiAutomationService.Query.cs#L39-L150) and [target resolver](https://github.com/microsoft/winappCli/blob/v0.7.1/src/winapp-CLI/WinApp.UIAutomation/Services/UiTargetResolver.cs) | Exact explicit HWND, fresh root each query/poll, root-limited selector, ambiguity detection using candidate bound of 2 + CompareElements; no arbitrary replacement HWND for explicit actions | Independent selector/readback comparator; unique-or-refuse principle | Runtime-derived element slugs are selectors, not durable identity or admission. Desktop-turn arbitration belongs to CLI, not underlying library |

The cross-project comparison is intentionally **versioned**. Latest upstream GitHub main is contextual prior art, not evidence for the pinned installed Avalonia 12.1.0, FlaUI 5.0.0 or WinApp 0.7.1.

## 3. Extracted algorithms, invariants and failure modes

### 3.1 Identity and authority are different data types

- **Authoritative session capability:** a process-local object reference checked by reference identity, revocable review plan, session, generation, scope, view and monotonic lease. See MetadataCaptureConsent and TraceNormalizer.
- **WebView native identity:** COM IUnknown identity for exclusive Network domain ownership. This is *not* a UIA element identity.
- **UIA observation target:** an ephemeral tuple of exact process ID, live HWND, declared root AutomationId, uniquely matched descendant AutomationId and current provider-generation. It is a locator only; it never grants capture, credential or provider rights.
- **WinApp runtime-ID slug:** in [upstream slug generator](https://github.com/microsoft/winappCli/blob/main/src/winapp-CLI/WinApp.UIAutomation/Services/SlugGenerator.cs#L92-L142), an unchecked multiply-31 hash is truncated to four hex digits. Treat such derived names as ephemeral conveniences, not collision-free identifiers, persistent resource IDs or security boundaries.
- **Record fingerprint:** canonical JSON encoding + hash or normalized structured fields for idempotency. Do not use delimiter-only concatenation when caller-controlled names can contain delimiters. SupraChat broker's earlier collision bug is already corrected on accepted main; do not reimplement the vulnerable format.

**Invariant I-1:** UIA action/readback refuses if PID/HWND/root/control identity disagrees, has zero matches, or matches more than once. Re-resolve after tab/window/provider changes; never silently broaden search to unrelated desktop windows.

**Invariant I-2:** A UIA locator or selected-state indication is never elevated to source-of-truth consent, signed-in human identity, provider operation permission or result-delivery authority.

### 3.2 Lifetime, concurrency and negative evidence

- Local capture state and durable semantic session state already use different locks/capabilities. Preserve that separation: a test should not create a new singleton/scheduler/ledger to coordinate them.
- **ReviewOperationFence** uses a generation counter: a late completion cannot apply after Close/Cancel. Keep an operation-fence concept separate from a UIA selector retry loop.
- **WindowsWebViewTraceAdapter** registers listeners before Network.enable; during attach it rechecks the current lease before/after SDK callbacks and eventually waits for Network.disable while retaining exclusive ownership until teardown settles.
- **FlaUI event callbacks** receive actual new property values in the callback. Callback registration must happen **before** the synthetic mutation, and the observer must report its registration-ready state before the parent triggers Fill. Do not store, render, or print the callback value.
- The UIA listener must use thread-safe atomics or bounded, fixed-code counters; callbacks from an external COM provider must not mutate application session state.
- **Event silence is not non-disclosure proof.** A bounded, non-secret property change must be observed as an **independent positive control** in the same listener before a missing sensitive event may be treated as meaningful. Also test removal/unregistration and no late callbacks after teardown. A provider that never emitted any event at all yields NOT_VERIFIED, not PASS.

**Invariant I-3:** A successful UIA event registration is not proven solely by a successful API return; retain a listener-ready handshake and an independently witnessed control event.

**Invariant I-4:** Real timeouts are monotonic. Await readiness, perform one explicitly admitted synthetic action, observe a bounded completion window, dispose handler, then check cleanup; never sleep indefinitely or poll stale elements until green.

### 3.3 Bounded data and provenance

- TraceNormalizer's **Queue** is bounded and reports evictions and explicit gaps; NativeMetadataEventProcessor limits raw JSON, request IDs and in-flight correlation **HashSet**. Reuse those patterns for **sanitized receipt status** but do not pipe raw UIA property values into TraceNormalizer.
- NativeMetadataLifecycleReadback is bounded to 24 most recent events; list head removal is O(n) but negligible at that bound. Reuse a bounded ring abstraction only if measured value justifies changing existing working code; do not invent a second persisted history.
- Current metadata summary saves through same-directory staging followed by no-clobber rename. A successful final file is a useful witness, not proof of physical power-loss durability or provider effects.
- Passphrase notification tests must retain only (a) callback count, (b) selected fixed property-ID counts, (c) synthetic-equality boolean, (d) bounded elapsed time, (e) source and target identities as booleans and (f) fixed enum failure codes. No raw event values, COM exception text, passwords, arbitrary names or URLs cross the evidence boundary.

**Invariant I-5:** No raw UIA callback contents, browser document content or credential bytes are ever emitted into CI artifacts, console logs, issue comments or public traces.

**Invariant I-6:** Distinguish NOT_READY, MISSING_ROOT, IDENTITY_MISMATCH, AMBIGUOUS_TARGET, EVENT_SOURCE_UNAVAILABLE, EVENT_LISTENER_NOT_PROVEN, OBSERVED_SAFE, DISCLOSURE_OBSERVED and CLEANUP_UNVERIFIED. Refuse a full PASS if a required channel is unverified. Unknown completeness stays UNKNOWN.

### 3.4 Identified source-level opportunities and concerns

| ID | Source-grounded observation | Severity and evidence | Next check |
|---|---|---|---|
| R1 | **Avalonia 12.1.0 stock TextBoxAutomationPeer exposes raw Owner.Text and raw Text-change events**, whereas SupraChat's custom peer suppresses Value provider and Text-change notifications. | Security-relevant implementation fact (S); existing filled synthetic scalar/legacy test passed (R) | Add explicit UIA event subscriber test without logging newValue; do not rely on newer Avalonia behavior |
| R2 | **FlaUI 5.0.0** supports native property/structure/notification handlers and IDisposable deregistration, but UIA3 callback delivers raw newValue. | Useful test primitive (S) plus sensitive data boundary | In a *separate disposable observer*, enforce callback-only comparison and prove event-listener liveness using nonsecret status |
| R3 | WinApp v0.7.1 selectors re-resolve fresh roots, require unique matches and guard explicit HWND; #93 separately compares placeholder/WebView tab behavior. | Reliability design already partly implemented (S); intermittent missing Auth.SignIn still not root-caused | Finish #93 differential receipts; classify owner HWND churn vs provider subtree reconstruction vs runner artifact, without weakening fails |
| R4 | MetadataCaptureConsent retains immutable plan results in a 64-entry Dictionary until Dispose; Preview checks capacity, CancelPreview and Confirm do not prune. | **H** possible long-lived workflow exhaustion; no reproducer or acceptance of cleanup policy yet | Synthetic 65-preview/cancel test, verify isolated/normal lifetime, then propose bounded historical outcome retention without breaking replay |
| R5 | App-owned lifecycle history is last 24 entries only; summary store has a durable local snapshot and explicit schema checks. | Known limitation (S) | Keep completeness UNKNOWN and use existing v3 aggregate; never claim full session events |
| R6 | Broker and ledger have fixed-size maps, canonical idempotency fingerprints and single commit/admission authority. | Reusable data structure patterns (S) | Do not couple native UIA observation to broker credential authority or create a second controller |
| R7 | FlaUI UIA3 and managed UIA observe the same window through different provider layers; the historical hand-built COM RCW returned zero PID/HWND but the pinned FlaUI client succeeded. | Historical first failure (R) and corrected adapter (R) | Preserve both receipts; use the existing qualified FlaUI adapter, not another manual COM binder |

## 4. Composition design: one authority, multiple observers and three audiences

**Not a new control plane.** Compose through the existing SupraChat semantic core, using explicit read-only test adapters:

1. **Owner/authorization layer:** existing SharedSessionLedger, MetadataCaptureConsent, LocalOwnedStreamControls and credential broker, unchanged in this research PR. Human review remains an authoritative prerequisite for consented first-party capture.
2. **Source adapters:** existing WebView2 passive native metadata, owned Responses/Codex transport traces and synthetic fixtures. UIA clients only observe the *application UI*; they cannot become the traffic or consent source.
3. **Typed projection layer:** existing TraceNormalizer, session ledger projections, the bounded lifecycle readback, validated v3 metadata summary. Do not create an unversioned parallel event database.
4. **Qualification layer:** test-only WinApp CLI 0.7.1 for independent semantic actions, managed Windows UIA for baseline comparison, FlaUI 5.0.0 UIA3 for legacy readback and event callbacks. Each has its own exact source/version and a process/HWND identity receipt.
5. **Audience interfaces:** app accessibility/GUI, existing CLI JSON and agent JSON-RPC expose the same admitted semantic operations and fixed-state readback, never three independently authorized backends.

Proposed small *test-only* records (not production public APIs): **TargetBinding** = process ID + HWND + expected root/control AutomationIds + bounded generation; **ListenerWitness** = listener-ready flag + control-event witnessed + property-ID counters + synthetic equality bool + cleanup-complete flag + monotonic elapsed; **QualifiedResult** = exact commit, binary/source hash, tool version, initial/final state, fixed failure code, scope and unknown coverage. A bind is transient: no stored runtime-ID, no credential bearer and no durable target authority.

## 5. Qualification sequence and safe parallel work

| Sequence | Executable work item | Parallelism | Acceptance/stop rule |
|---|---|---|---|
| A | Finish #93 source-vs-runner UIA root-loss comparison (placeholder then WebView, managed client vs WinApp CLI) | Separate from other source checks | Preserve first failure, exact UIA target ID and HWND/bool diagnosis; no real account |
| B | Add a *synthetic-only* event witness to the existing UIA3 observer, with positive control on a non-secret app-owned status property | A and C independent; B internal sequence required | Observer registered → ready → synthetic change → observed control event → sensitive-channel evaluation → handler disposal; fail if any gate missing |
| C | Run a 64/65-preview negative test of MetadataCaptureConsent; compare session lifetime and replay semantics | Parallel with A/B | Show exact first error and cleanup behavior before proposing pruning/compaction |
| D | Extend fixed-schema independent evidence validator for A/B/C outputs | After schema choices; separate from application auth | Reject extras/duplicates, unknown enum states, missing identity/ready/cleanup evidence and raw values |
| E | Reconcile source changes with current accepted main and source-only review before any future feature PR | After A-D | Exact-head cross-OS and Windows native CI; no first-party release claim |
| HIL | Actual original-host signed-in WinApp v3 lifecycle/UIA qualification | Sequential, *separately held* | Only on an actually accessible, authorized Windows host; not admitted by public GHA synthetic success |

**Three-audience requirement:** Human-facing UI should show bounded state and failure reasons without secrets; automation needs machine-readable stable schema and strict versioning; agents need the same non-privileged read-only readback and explicit admission for any actuation. If a native event observer is offered to all audiences, its data flows **through the existing semantic projection**, not a new power-conferring UIA command surface.

## 6. Scope and source provenance

Pinned external implementations read for this research:
- [Avalonia 12.1.0 TextBoxAutomationPeer](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Controls/Automation/Peers/TextBoxAutomationPeer.cs) — returns raw text and raises raw ValueProperty on changes.
- [FlaUI 5.0.0 UIA3Automation](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.UIA3/UIA3Automation.cs), [UIA3FrameworkAutomationElement](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.UIA3/UIA3FrameworkAutomationElement.cs), [UIA3PropertyChangedEventHandler](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.UIA3/EventHandlers/UIA3PropertyChangedEventHandler.cs), [EventHandlerBase](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.Core/EventHandlers/EventHandlerBase.cs).
- [Microsoft WinApp CLI 0.7.1 selector engine](https://github.com/microsoft/winappCli/blob/v0.7.1/src/winapp-CLI/WinApp.UIAutomation/Services/UiAutomationService.Query.cs), [window resolver](https://github.com/microsoft/winappCli/blob/v0.7.1/src/winapp-CLI/WinApp.UIAutomation/Services/UiTargetResolver.cs), [UI automation documentation](https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md). Latest main documentation and runtime-ID slug algorithm are useful background but may differ from 0.7.1.
- [Current SupraChat synthetic UIA observer](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/SupraChat.UiaComObserver/Program.cs); [current passphrase test](https://github.com/SemperSupra/suprachat/blob/a0fa56c943d73482ac233834b96f31fb591fd385/tools/local/Test-SupraChatSecretUiaProbe.ps1); [#93 UIA root diagnostic](https://github.com/SemperSupra/suprachat/pull/93).

No repository-wide algorithmic equivalence, timing envelope, first-party login state, provider effect, complete credential confidentiality, or production readiness is claimed. Re-run every qualification at the exact resulting head; never convert an unexecuted/failed/optional test to PASS.
