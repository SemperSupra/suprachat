# Native consent-bound metadata recorder — #51

This W12 candidate stacks on the independently qualified consent prototype #65 and
reuses the native WebView2 observer from #60. It makes an actual native event
source pass through the session/view-bound consent gateway, without enabling
first-party ChatGPT capture.

## Scope and security invariant

Only a fresh **synthetic loopback** WebView may use the new
`WindowsWebViewTraceAdapter.AttachConsentSyntheticAsync` entry point.
The existing `AttachSyntheticAsync` method remains unchanged for independent
legacy fixture qualification. It must not be mistaken for consent protection.
Neither method accepts `chatgpt.com` or another first-party/remote origin.

`MetadataCaptureConsent.RegisterNativeSynthetic` creates a distinct local
binding. The ordinary fixture binding cannot inject a WebView2 observation.
Both still require explicit acknowledgement of a short-lived synthetic
metadata review. The new adapter verifies the exact local trace session,
consent binding and view before attaching. Every native callback passes through
a consent-bound sink that rechecks the same binding, view, lease and synthetic
surface; no raw write to the normalizer is performed by this new path.

After consent stops/expires/view replacement, the callback gate refuses
subsequent observation and initiates native detach. The embedding owner can
also call `StopObserving`/DisposeAsync immediately rather than awaiting an
event. No generic CDP command, raw traffic body, cookie, credential or actual
first-party request is exposed. A consented native fixture remains
`SYNTHETIC` evidence and retains `UNKNOWN` completeness.

The existing consent gateway deliberately does not intercept other trusted
code holding a raw normalizer capability; any eventual first-party integration
must use the gated sink exclusively and deny direct writes. This candidate
does not add an authenticated human identity or production first-party
entry point. It also does not make the temporary in-memory consent state
durable across restart.

## Corrective lifecycle gate (October 25, 2026)

The gateway now issues a one-shot task for the exact active view/lease and
completes it for explicit stop, renewed review, view replacement, recorder
loss, expiry, and disposal. The native adapter observes that task separately
from CDP callbacks and marshals its cleanup to Avalonia's UI dispatcher.
This prevents an otherwise idle page from retaining an active CDP Network
subscription until another browser event occurs. A per-native-lease
`TimeProvider` timer enforces the 120-second expiry even without a gateway
poll; the existing monotonic check remains defense in depth.

Revocation never invokes WebView COM under the gateway's lock. The event
ingestion gate still checks each callback; immediate refusal and eventual
detachment are distinct assertions. Native domain disable and COM cleanup
remain asynchronous and must be verified separately. No live first-party
origin is admitted. The legacy direct synthetic fixture attachment is
unchanged and is **not** protected by this consent feature.

The Windows synthetic probe requires `IsStopped` during an idle interval
after stop, with no further fixture requests. Cross-platform contract
tests verify one-shot signal completion for stop, renewed approval,
monotonic expiry, view change and disposal. CI success is still not a
human screen-reader review or live product authorization.

## Qualification

Cross-platform contract tests add native-source negative cases to the
consent state machine: OFF, default-fixture source denial, native view/binding
mismatch, authorized synthetic event, rejected ChatGPT/Responses surfaces,
and stop/expiry refusal. The Windows WebView2 native fixture binds the second
of two independent synthetic views to an actual acknowledged consent lease.
It requires refusal before review, successful native observation afterwards,
and zero newly retained frames from an explicitly stopped source. The separate
native observer validates that normal page behavior is preserved, with the
existing closure/disagreement classification unchanged.

The Windows workflow must assert the sanitized
`native_synthetic_consent` and `native_consent_stopped` booleans.
Public free-tier GitHub Actions provides exact-head Windows native and
Windows/Linux/macOS contract/product qualification; no live ChatGPT profile,
password, provider traffic or synthetic sentinel enters receipts.

## Remaining admission

Do not generalize this bridge to a signed-in origin by changing an allowlist.
A separate owner-visible, explicitly authorized first-party consent route,
independent review of capture scope and privacy, native HIL with redacted
receipts, production identity/custody checks, and durable session admission
are still required before first-party traffic observation can be claimed.
Live request modifications and payload capture remain prohibited.
