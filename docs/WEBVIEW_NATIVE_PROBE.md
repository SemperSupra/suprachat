# Windows native observation feasibility (issue #51, T1)

This independent candidate starts from canonical main `250e5ef9d2a3017e8374d1a8243429abcc54eaf0`.
It adds a standalone self-testing executable, not a hook in the authenticated product WebView.
It never constructs the product MainWindow, credential store, ChatGPT URL, OAuth client or a
remote debugging listener. There is no arbitrary-URL, arbitrary-CDP-command or browser-profile option.

The probe creates two Avalonia 12.1.0 NativeWebViews with fresh, separate temporary data folders
and InPrivate profiles. Interactive DevTools remain disabled. Known environment overrides that
could redirect profile/browser/debugging configuration cause an explicit refusal before startup.
The normal SupraChat browser profile is never opened or copied. Synthetic temporary profiles remain
under the OS temp directory; they are not uploaded as evidence.

## What is tested

After AdapterCreated, TryGetPlatformHandle supplies IWindowsWebView2PlatformHandle.CoreWebView2.
The pinned Microsoft.Web.WebView2 1.0.4258.31 SDK's public CreateFromComICoreWebView2 factory wraps
that existing handle. The Core managed assembly is referenced explicitly so the SDK's unrelated
WPF/WinForms/WinRT host targets cannot change Avalonia browser ownership or add host dependencies.

Each browser observes its own synthetic Kestrel loopback fixture. CDP Network.enable plus typed
event receivers observe request/response/data/terminal events, WebSocket sent/received frames and
EventSource messages. Native WebResourceRequested and WebResourceResponseReceived independently
count HTTP routes. Handlers do not modify responses, supply bodies or block fixture requests.

The fixture verifies exact streamed bytes, two EventSource messages, a WebSocket echo and an
explicitly aborted fetch. URLs, a request header and payloads contain a synthetic secret sentinel.
Only closed event/route labels, counters, booleans and runtime version enter the receipt. Raw CDP
JSON, headers, URLs, payloads, exception messages and upstream request IDs are never written.
Correlation tables have a hard 64-request bound; correlation aliases are not exported.

Two concurrent views must remain isolated. Each observed route must match exactly one server
request and each HTTP request/response must match both CDP and native resource events. After all
subscriptions/filters are removed and Network.disable completes, both views run the fixture again.
Phase-specific page completion prevents the previous document from satisfying the detached test.
No observer callbacks may fire after detach; the second fixture must still pass all application checks.

## First failures and qualification limits

Local Windows runtime **155.0.4283.62** exposed a repeatable discrepancy: the JavaScript reader
successfully consumed the exact finite stream body, but CDP reported Network.loadingFailed with
ERR_ABORTED for that stream. The initial strict loadingFinished assertion failed. The discrepancy
survived replacement of HttpListener with Kestrel, removal of request interception, CDP-only
observation, fixed-length responses and serializing explicit cancellation after stream consumption.
The cause remains unresolved; it is not attributed to Microsoft, Avalonia or the fixture without proof.

The resulting oracle requires one observed terminal event (end OR error), matching issue #51's
observation contract, and explicitly reports stream_lifecycle_disagreement. It does **not** reinterpret
an observed abort as successful network completion. Completeness is always UNKNOWN and
production_admitted is always false. A successful exit qualifies **native interface feasibility only**;
it does not clear full T1 production admission, signed-in ChatGPT capture, provider-completion fidelity,
failure recovery, all platform event families or first-party traffic modification.

Local evidence progressed from first-native-receipt.json through a bounded-feasibility-receipt.json
under the workspace's `_dogfood/trace-webview-t1` directory. The latter demonstrated two independent
views, all required event families, exact fixture checks, and zero callbacks after detach. Genuine
first-failure receipts remain local and unchanged. No real traffic or credentials were used.

## Run

From the repository root in PowerShell 7 on Windows with WebView2 installed:

```powershell
dotnet build SupraChat.WebViewProbe/SupraChat.WebViewProbe.csproj -c Release
./tools/local/Test-SupraChatWebViewProbe.ps1 -ReceiptPath "$env:TEMP/fresh-webview-probe-receipt.json"
```

Use a fresh receipt path for every rep. The runner enforces a 45-second process deadline and checks
receipt privacy/scope independently of process aliveness. It kills only its own timed-out child process.
The dedicated public Windows workflow runs the same synthetic probe and uploads only the sanitized
receipt. The normal product build remains independent of this experimental project.

## Source used to expedite implementation and diagnosis

- [Avalonia native interop](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)
  defines the native handle boundary and COM ownership.
- The installed Microsoft SDK supplies WebView2.idl and build/native/include/WebView2.h. Those are
  interface definitions, not the closed-source Edge/WebView2 browser implementation.
- Microsoft's [ScriptComponent.cpp](https://github.com/MicrosoftEdge/WebView2Samples/blob/main/SampleApps/WebView2APISample/ScriptComponent.cpp)
  shows CDP domain enablement, event receiver retention, registration and teardown. The probe uses
  that lifecycle pattern without the sample's raw event display.
- The matching .NET 10.0.12 [Windows HttpResponseStream source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.HttpListener/src/System/Net/Windows/HttpResponseStream.Windows.cs)
  established fixed-length/chunk termination behavior during fixture isolation. The fixture now uses
  Microsoft's Kestrel server; no Microsoft source files are copied into the product.

## Independent comparison — 2026-10-24

SupraChat.WebViewOracle now runs the same fixture in a fresh Playwright Chromium context and a
minimal Microsoft WinForms WebView2 host without Avalonia. Runtime versions were Chromium
153.0.8010.12 and WebView2 155.0.4283.62. The Microsoft-only host reproduced the abort, ruling out
an Avalonia-only explanation. An initial Chromium rep reported normal loadingFinished, but repeated
Chromium reps also reported ERR_ABORTED while consuming the exact complete body. Therefore an
engine-specific explanation is not established. Retaining the Response and reader in the synthetic
page did not eliminate the discrepancy. Original receipts are preserved; no outcome is rewritten.

The initial Playwright polling helper also conflicted with the fixture CSP's eval restriction. The
oracle now polls the title from the host, without weakening CSP or changing stream traffic. The
matching pinned Playwright headless runtime is installed explicitly. The comparison script checks
both page completion and exactly one observed terminal event per finite stream; it records the
actual success/error distinction rather than requiring the two browsers to agree. This comparison
does not prove why an abort was emitted or infer remote provider success.

Run after building the oracle project and installing its pinned browser:

```powershell
dotnet build SupraChat.WebViewOracle/SupraChat.WebViewOracle.csproj -c Release
./SupraChat.WebViewOracle/bin/Release/net10.0-windows/playwright.ps1 install chromium --only-shell
./tools/local/Compare-SupraChatWebViewOracle.ps1 -OutputDirectory "$env:TEMP/fresh-oracle-receipts"
```

Receipts contain only closed fixture route/event counters, runtime versions and scope/boolean
classifications. Fresh profiles are used, and known WebView2 environment overrides are refused.
The Windows workflow repeats the independent comparison and preserves sanitized receipts only.

Public rep 40060422171 exposed a separate test assumption: the browser coalesced two flushed server
writes into one dataReceived event even though the exact body was consumed. The fixture now waits
for an explicit page POST acknowledgment after the first reader chunk before sending the second.
Server request counts and exact body verification prove progressive consumption independently of
CDP event batching. At least one data event and exactly one terminal event remain required. This
replaces a timing-sensitive packet-count assertion with a causal application-level oracle.

Next: independent review and native adapter integration; preserve unknown completion confidence
unless a separate semantic source proves provider completion.
T2 owned Responses/Codex instrumentation may progress independently against the shared T0 contract.
No signed-in ChatGPT observation or live first-party actuation is admitted by this experiment.
