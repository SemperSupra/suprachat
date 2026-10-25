# Native metadata consent integration — issue #51

Stacked on the synthetic consent prototype (#65). The Windows observer now uses the consent
gateway for native producers. Public fixture admission remains fixture-only; the internal native
route requires the exact recorder capability, current view reference, current plan and unexpired
lease. Historical ALLOWED receipts cannot start another observer or extend a lease.

## Qualified scope

The disposable WebView2 probe qualifies two loopback views with real native callbacks. It covers
default OFF, missing acknowledgement, independent sessions, stop, idle expiry, stop/expiry while
Network.enable is pending, navigation to about:blank, and a host handle getter switching to another
actual CoreWebView2. The last check models host view replacement; it is not a claim of Avalonia
adapter recreation. All observations remain synthetic. Headless contracts also cover exact-origin
admission, producer/plan isolation, asynchronous review close/stop fencing and cleanup publication.

Canonical IUnknown ownership remains reserved until registration, enable and disable finish.
An independent timer revokes ingestion on expiry; COM cleanup runs on the owner UI dispatcher.
Every callback checks consent before requesting raw event JSON and the recorder checks again
before retention. Stop and navigation revoke the current plan before asynchronous cleanup.

## Prepared first-party entry point (not live-qualified)

On Windows, the explicit command-line flag `--first-party-metadata-hil` exposes a local review button.
The flag alone attaches no observer. The local operator must review the scope, acknowledge it,
type `ALLOW METADATA`, prepare the current view and confirm within 60 seconds. Capture lasts at
most 120 seconds. Any navigation, native view change, stop or review-window close ends capture.
The controller requires a loaded view at the exact HTTPS chatgpt.com origin and rejects known
WebView2 environment overrides. The admission origin is not a sign-in or conversation identity.

This is a single-operator experiment with an ephemeral local ledger session, not the production
shared host, durable issuer, cross-actor privileged export or human identity admission. The typed
acknowledgement is an interaction safeguard, not proof that a human authorized an operation.
The normal profile is used only if an operator explicitly elects to run this path. Qualification
does not launch this flag or access a signed-in profile. Source review and explicit operator opt-in
are required before the first signed-in metadata HIL under issue #51.

Network.enable subscribes to the whole view's network domain. Raw CDP JSON, including off-origin
requests and payload-bearing fields, may transiently reach this process before origin filtering.
The exact-origin restriction applies to retained metadata, not all raw events entering memory.
Only allowlisted timing,
direction, status, byte counts, origin category and pseudonymous correlation reach the bounded
256-event in-memory recorder. Headers, cookies, URLs, prompts, responses and frame payloads are
not retained or exported. This is not a claim that sensitive fields never enter process memory.
There is no generic CDP command, provider action, replay, traffic modification or automatic upload.

The explicit save action stops capture, then offers an aggregate-only new JSON file. It exports no
event arrays, correlation aliases, URLs or titles. Diagnostic events alone cannot label evidence as
first-party observations. A nonzero admitted network-event count labels the summary
FIRST_PARTY_LIVE_REDACTED; sign-in remains OPERATOR_CHECK_REQUIRED, completeness UNKNOWN,
and provider-operation, conversation-binding and human-verification claims remain false.

## Remaining gates

1. Exact-head independent review and public Windows native / full product CI.
2. Explicit operator opt-in, followed by the first signed-in metadata-only Windows HIL. Review the
   actual UI and aggregate summary; no current synthetic receipt satisfies this gate.
3. Durable provider-control approval and effect receipts, production issuer/host/custody/human
   admission, and actual second-account and hostile owner/path/race/rollback qualification.
4. Native macOS/Linux adapter research and any separately consented encrypted payload storage.
5. Owner-authorized stack integration, merges and release qualification. No merge or release is
   authorized by this packet.
