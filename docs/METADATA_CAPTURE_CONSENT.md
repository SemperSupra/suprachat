# Per-session metadata consent prototype — issue #51

This document describes the fixture-only prototype. Its subsequent guarded native integration is
described in [Native metadata consent integration](NATIVE_METADATA_CONSENT_INTEGRATION.md).

Stacked on #64. This packet implements a real review/confirmation/stop workflow in the isolated
workbench, backed by the shared core. It admits only Synthetic/Fixture observations. It neither
authorizes a human nor enables a first-party native entry point. The existing WebView adapter still
refuses first-party origins and is not connected to this gateway.

## Contract

`MetadataCaptureConsent` registers fresh OFF recorder sessions using exact process-local capabilities.
Plans and bindings are reference capabilities; serializing a review or knowing its UUID cannot apply it.
The plan binds one authoritative session, current view revision and review epoch. At most 16 bindings
and 64 retained reviews/outcomes are allowed; capacity exhaustion refuses new reviews.

Review expires after 60 seconds and a confirmed synthetic lease after 120 seconds, measured with
monotonic timestamps. UTC dates are display information only. Confirmation requires acknowledgement
and the original matching target. Matching-target first outcomes are immutable: retrying an allowed
plan returns the historical receipt and never restarts or extends capture. Wrong-target authority
refusals do not consume another session's plan and are explicitly marked outside that outcome cache.

Stop, actual view replacement, recorder stop/failure and lease expiry disable ingestion. Stale pending
reviews cannot restart it. Session selection cancels the pending review and clears acknowledgement;
independent active session leases are otherwise independent. View replacement also rejects the old
producer's view reference. First-party, payload and unknown scopes remain unavailable even after
acknowledgement. Review acknowledgement proves no human identity or live operator authorization.

Every producer must use `TryObserve` on this gateway. Checks and recorder writes share its lock;
stop and writes therefore have a defined order. This does not revoke direct normalizer capabilities
held by trusted owner code, authorize existing native adapters, or establish a first-party capture gate.
Those integrations require a separate reviewed entry point. Expiry is enforced on gateway calls;
the prototype UI also polls status. Native/raw sinks must not be represented as protected by it.

Previously retained metadata remains readable after stop, as the UI states. Buffering and gap accounting
stay in the existing bounded normalizer; no payload vault, trace persistence or automatic upload exists.
Consent state and plans are volatile and are not written to the session checkpoint or restored after
process restart. The recorder remains separate from session mutation authority.

## Human and machine surfaces

The optional `--metadata-consent-receipt` workbench starts both sessions OFF. It displays a readable
session/view summary, retention/scope explanation, review details, unchecked acknowledgement,
disabled confirmation and explicit stop. Switching sessions clears review; stale/expired reviews are
disabled and must be renewed. The controls are stable accessible peers under Consent.* IDs.

Only the local fixture GUI holds confirmation plan objects. Enrolled CLI and JSON-RPC clients receive
`consent/status` through the existing scoped read/admission/revocation gate; no machine confirmation
or payload method is exposed. Host lock order is ledger→consent→normalizer. The fixed synthetic
producer uses the consent gateway; direct initial production is skipped in this mode. Default shared
workbench behavior is preserved and tested independently.

The test-only expiry button advances the workbench clock by three minutes. Its provider also includes
real elapsed monotonic time. This proves expiry handling without waiting two minutes; it is not an
elapsed-wall-time or signed-in HIL receipt. Actual first-party availability is shown as unavailable.

## Qualification

Cross-platform contracts exercise default OFF, missing acknowledgement, immutable retries, wrong
session/view/controller/plan, first-party/payload/unknown scope refusal, monotonic review and lease
expiry despite UTC rollback, stop invalidation, independent sessions, view replacement, bounded
storage/gaps, review capacity, disposal and recorder failure. Sentinels stay pseudonymized.

Real own-window UIA exercises unapproved event refusal, acknowledgement, session-switch cancellation,
reviewed starts, stop, first-party refusal, expiry, replacement and renewal. It then runs the shared
automation/JSON-RPC peers and checkpoint recovery. Both machine readers must observe capture OFF.
Expected receipts contain three reviewed synthetic starts, at least five refused observations, and one
retained synthetic event per session. Only sanitized host/UIA receipts enter public/free CI.

```powershell
dotnet run --project SupraChat.ContractTests -c Release
dotnet build SupraChat.PeerLedgerRdte -c Release
./tools/local/Test-SharedDesktopLab.ps1 -Consent -ReceiptPath "$env:TEMP/fresh-consent-receipt.json"
```

Fresh local contracts and actual UIA passed. Independent exact-head review and public Windows consent,
default workbench, custody/peer, native WebView and tri-OS product regression precede acceptance.
No source is promoted to main by these results.
One repeated UIA rep failed because the session popup item was queried immediately after expansion.
That receipt is preserved. The helper now waits at most five seconds for a selectable item, records
the wait count, and still fails if the semantic item remains unavailable.

## Next gate

Implement and review a separately scoped first-party adapter/consent integration, then obtain explicit
operator opt-in before signed-in metadata-only HIL. This prototype is not that opt-in, and its GUI
qualification cannot grant it. Durable provider-control approval/effect receipts, production identity/
custody/host services and owner merge/release remain separate gates. No real credentials, provider
inference, signed-in capture, profile export, first-party modification or paid resource is used here.
