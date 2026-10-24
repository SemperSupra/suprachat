# Native WebView2 to shared trace — issue #51

This candidate stacks on governed-control #59. It adds a production-assembly native metadata
adapter, but current admission is deliberately **synthetic loopback only**. It does not attach to the
normal ChatGPT view, open its profile, authorize signed-in capture or modify provider traffic.

WindowsWebViewTraceAdapter wraps the owner-provided CoreWebView2 handle with the pinned SDK's
public factory. The caller must hold the exact local trace capability for the supplied normalizer.
The origin must be an absolute loopback HTTP(S) root without userinfo/query/fragment, and the native
view must be blank or already at that origin. A first-party origin or foreign session is refused before
subscribing. Leaving the admitted origin detaches and reports a capture gap; navigation is not blocked
or rewritten. The adapter has no arbitrary CDP-command, cookie, body-fetch, replay or request-write API.

## Common metadata contract and bounds

Native request/response/data/terminal, WebSocket and EventSource events normalize directly into
the same suprachat-trace/v1 implementation used by the desktop, automation and agent readers.
All fixture events are labeled Surface=Synthetic and Adapter=WebView2. OBSERVED never means
APPLIED or VERIFIED provider operation. loadingFailed stays Error; it is never rewritten to success
because the fixture page reports a complete body. Completeness remains UNKNOWN.

Only origin admission, upstream correlation IDs, HTTP status and dataLength are inspected. The
adapter does not read header collections, post bodies, WebSocket payloadData, EventSource message
data or arbitrary error strings. No raw CDP JSON is logged or persisted. Upstream IDs are retained
only in a bounded active-request set and become per-run/session/domain aliases at the normalizer.
The set is capped at 256 IDs of at most 512 characters; terminal events remove entries. Oversized
JSON over 1 MiB, malformed metadata, correlation capacity exhaustion, scope exit and sink exceptions
disclose loss rather than fabricating completeness. Capability revocation/disposal stops observation.

The native Network.enable call sets a 64 KiB total preservation buffer, 16 KiB resource preservation
buffer and zero post-data inclusion limit. These are CDP preservation limits, not proof that every
internal browser allocation is bounded. The normalizer separately bounds retained event storage.
See the [official Network.enable contract](https://chromedevtools.github.io/devtools-protocol/tot/Network/).
The adapter requests no response bodies. Recorder exceptions do not propagate into browser event
callbacks. Calls and disposal belong on the owner WebView UI thread; Avalonia retains native ownership.

An IUnknown identity lease admits one Network-domain owner per native view (at most 16 views).
Duplicate attachment is refused. Detach removes handlers immediately and releases that lease only
after the pending enable and subsequent disable settle; a timed-out cleanup retains ownership.
Repeated disposal cannot disable a replacement observer. Other components that independently issue
Network.enable/disable on that view are unsupported; the qualification counter is passive and issues
neither command. Controllers track their adapters, stop them on disposal and report actual attachment
and metadata capture state, with SYNTHETIC_LOOPBACK_ONLY scope. Cross-origin redirects revoke a
previously admitted request ID before later response/data events can be attributed to the fixture;
response URLs are independently checked and revocation reports a gap.

## Native qualification

The dedicated Windows probe references the actual product assembly. Two fresh InPrivate views emit
typed shared-core events while an independent native event counter observes the same fixture. It checks
session isolation, request/status metadata, exact body behavior, explicit first-chunk acknowledgment,
secret-sentinel exclusion, zero happy-path loss and no new typed events after detach. A third fresh view
qualifies wrong-session refusal, first-party admission refusal and automatic gap/detach on an offline
out-of-scope navigation. No authenticated application window or real provider request is used.
It also checks duplicate-owner refusal, controller-driven detach, repeated old disposal after
replacement, and a real redirect to a second loopback origin whose distinctive 201 response must
never appear in the admitted origin's trace.

```powershell
dotnet build SupraChat.WebViewProbe/SupraChat.WebViewProbe.csproj -c Release
./tools/local/Test-SupraChatWebViewProbe.ps1 -ReceiptPath "$env:TEMP/fresh-shared-native-receipt.json"
```

The runner requires NATIVE_SHARED_TRACE_SYNTHETIC_ONLY, common schema metadata, zero happy-path
loss, progressive acknowledgment, scope/authority negatives, and production_admitted=false. Local
native qualification passed; public Windows exact-head qualification and independent review are required.
The product's complete tri-OS contract/transport/CLI/agent/UIA workflow remains mandatory.

## Remaining signed-in and integration gate

An independent oracle now exists in #53 (Playwright Chromium and Microsoft-only WebView2). Some
reps finish normally and others report ERR_ABORTED despite application completion; causation is not
established. Data event batching is handled by a causal first-chunk acknowledgment instead of a fixed
packet-count assertion. No platform-only cause is claimed and no network terminal state is invented.

The next admission step is independent source/security/privacy/accessibility review of this exact adapter
and a concrete owner-visible consent flow. Only after that review and explicit operator opt-in may a
separately scoped first-party metadata entry point and signed-in local HIL be enabled. The current API
cannot be pointed at chatgpt.com. T4 must reuse the selected W6 session authority and qualified W8
identity; this adapter and the trace recorder cannot become another session mutation owner.
