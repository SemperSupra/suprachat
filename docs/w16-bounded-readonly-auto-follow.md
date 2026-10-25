# W16 — explicitly started bounded host metadata auto-follow

Authority: [SupraChat #19](https://github.com/SemperSupra/suprachat/issues/19).
Starting point: [W15 host epoch](https://github.com/SemperSupra/suprachat/pull/66).
This is an experimental **read-only** projection over an existing W6 loopback host,
not a synchronized ChatGPT product conversation, user identity, or production DLE
subscription. No new database, bearer issuer, scheduler, browser bridge or provider.

## UX, DX, and authority

The human first manually reads an explicitly selected 127.0.0.1 host and selects
one observed host-session GUID. A separate accessible **Start bounded read-only
auto-follow** action requires the same selected session, previously observed
global sequence and canonical host process epoch. **Stop auto-follow** cancels
in-flight HTTP and the observer loop. Manual refresh, session selection changes
and window close also cancel the run. The host port must match the observed port.

Exactly the already qualified `SharedSessionFeedProjection` applies to every
sample. There is no silent switch of selected session, implicit creation,
mutation, browser navigation, ChatGPT product read/write, or terminal execution.
CLI `session-ledger-follow` and agent `session/ledger/follow` retain the same
single-observation projection semantics; no new machine-side background daemon
or unbounded stream is introduced.

## Execution/resource policy

- **Explicit opt-in per invocation**, off at startup, no persistence or auto-resume.
- One outstanding read and one awaited presentation callback at any time.
- Fixed two-second interval, max 64 samples per human start; observer refuses
  invalid time intervals/max limits in the pure core. Subsequent continuation
  requires another explicit Start.
- Each HTTP GET stays loopback-only, cookie-free, redirect-free, proxy-free,
  64-KiB body bound and four-second **whole-response** deadline. It now accepts
  a caller cancellation token linked to that deadline.
- Caller-held epoch/selected-ID/cursor never change across host replacement.
  Sequence rewind, missing session, truncation, missing/nonmatching epoch,
  host failure or malformed host metadata **terminate**; never treat failure
  as a continuity success, never retry uncertain mutations.
- A successful sample advances the caller's cursor only after consumer
  presentation finishes. Observer receipts never include prompts, results,
  cookies, bearer tokens or exception details.
- GUI uses semantic Start/Stop/status control IDs and a polite live status;
  no screen-reader user is forced to inspect a graphic indicator.
- W15 process epoch is unverified self-report; malicious same-user replacement
  remains outside this experiment's identity guarantee.

## Qualification

`dotnet run --project SupraChat.ContractTests/SupraChat.ContractTests.csproj -c Release`

Deterministic W16 tests: explicit selection and accessible controls, two
consecutive reads with no duplicate event, max bound, same-sequence host change
fencing, missing retained history, cancellation before read, presentation
backpressure (a second read is withheld), and sanitized exception stop.

CI must also preserve W15's real two-session host test, hostile inventory,
W13 scope/revocation, W9 durability and full 3-OS desktop packaging/UIA.
Synthetic CI passing **does not** admit ChatGPT product turn access, live
browser traffic observation, real Windows hostile-peer identity, or full
historical completeness.

## Further work and stop conditions

Next candidate: a single-W6-ledger authenticated OS principal/issuer and
owner-issued revocation integration, **before** selected native desktop tabs
can mutate host state. Only separately approved official ChatGPT MCP plugin
interchange may associate a real ChatGPT conversation with a DLE workstream.
Full automatic ChatGPT turn history synchronization remains blocked without
a supported and authorized interface.

Stop the experiment on evidence of unbounded reads, multiple simultaneous
runs from one window, loss of Stop cancellation, duplicate presentation after
reconnect, metadata leaks, or an epoch mismatch that does not clear selection.
