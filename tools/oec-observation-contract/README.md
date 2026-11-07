# OEC observable-surface contract v1

This workcell validates **public-safe, metadata-only** user-interface observation receipts. It does not connect to a signed-in browser and is not a bridge implementation.

## Supported evidence
- `PUBLIC_SYNTHETIC_NATIVE`: observed on a disposable public CI runner. Authentication state is `NOT_ASSESSED`.
- `USER_ATTESTED_LOCAL_UIA`: observation supplied by a local operator who explicitly attests to having authenticated. Login is **not** independently verified by the schema.

Only fixed booleans and bounded counts are retained. No text/values, URLs, window titles, screenshots, cookie stores, bearer tokens, API keys, DOM content, session identifiers, process identifiers, or unrestricted browser access.

## Three audiences
- Human: exact PASS/FAIL gates and separate login evidence classification.
- Automation: strict schema, no duplicate/extra fields, deterministic JSON bytes and fail-closed negatives.
- Agent: proposed read-only `observe_environment`, `inspect_ui`, and `compare_state` capabilities; `remote_control=false`.

## Qualification
`python3 -m unittest discover -s tools/oec-observation-contract -p 'test_*.py' -v`

Cloud CI qualifies the validator on public synthetic fixtures. It does not prove authenticated-WebUI behavior, ownership of local login, or any OpenAI tunnel association.

## Integration sequence
1. Operator signs into local SupraChat and explicitly approves observation. Do not move authentication credentials to CI or public artifacts.
2. Local Windows UIA actor obtains only bounded counts and fixed-ID presence, never raw UI text/DOM or screenshots.
3. Validator rejects any unacceptable fields and labels user-attested evidence separately from direct technical observation.
4. After admission, a separate narrow read-only MCP server may make this receipt available through OpenAI Secure MCP Tunnel. Requires a tunnel ID, runtime API key and account/workspace connector permission. The **earlier refused** `tools/oec-auth-bridge/main.go` write remains refused and is not replaced here.
5. Test unauthorized calls, replay/expiry, credential leakage and teardown before any live authenticated tunnel use.

**Authority:** SupraChat private DLE #2. No native code changes, merges, production credentials, private Actions minutes, release, or deployment.

## Existing first-party WebView2 metadata primitive (reuse, do not rebuild)

Canonical main already implements a consent-gated **Windows** WebView2 metadata capture with a native review window. Relevant source: `SupraChat/MainWindow.axaml.cs`, `SupraChat/Core/WindowsConsentedMetadataSession.cs`, `SupraChat/Core/NativeMetadataSummary.cs` and `NativeMetadataSummaryStore.cs`.

Source-derived safeguards: exact `https://chatgpt.com/` origin, in-process view identity, prepare/review/confirm, a 120-second lease, stop on navigation/view replacement/expiry, no automatic upload, and atomic no-overwrite local aggregate persistence. The capture is **network metadata**, not conversation/DOM content. It does not verify login or infer model internals.

To activate *only after authorized local source review* on a signed-in Windows SupraChat instance:

```powershell
$out = Join-Path $env:LOCALAPPDATA 'SemperSupra\SupraChat\metadata-experiments'
& '.\SupraChat.exe' '--first-party-metadata-hil' '--open-metadata-review' "--metadata-summary-directory=$out"
```

The command opens a review window; the operator must acknowledge, prepare and confirm separately. Capture remains OFF until confirmation. Stop/save and close the review normally. The original aggregate is private local evidence; do not attach it to a public GitHub issue or public runner.

After capture, optional **local-only** minimization for a specific saved `suprachat-metadata-summary-*.json` file:

```powershell
python .\tools\oec-observation-contract\export_native_summary.py 'C:\PATH\TO\APP-AUTHORED-SUMMARY.json' > '.\sanitized-projection.json'
```

`native_summary.py` verifies the app-authored first-party v3 source schema, consent-lifecycle agreement, safe end state and no-export flags, then emits only aggregate counts, failure classifications and SHA-256 of the source file. It explicitly sets `authenticated_state=OPERATOR_CHECK_REQUIRED` and `verified_provider_action=false`; it never exports raw events, diagnostics, URLs, cookies, credentials or content. A failed validation produces no projection.

The separate PowerShell 5.1 `Inspect-SupraChatStructure.ps1` optionally provides structural counts for `Tab.ChatGPT` and `Tab.Sessions`. Public CI uses `-SyntheticFixture`; a local operator must use `-OperatorAttestsSignedIn` and supply the authorized SupraChat ProcessId. That attestation **is not independent login verification**. This source-isolated local one-shot is not an MCP bridge.

## Connection admission

The independently qualified public Cloudflare cross-runner MCP transport does not grant signed-in browser access. For OpenAI-native direct ChatGPT tool calls, use the upstream `openai/tunnel-client` Secure MCP Tunnel with a locally reachable MCP server, tunnel association, runtime API key and account/workspace permission to create a custom MCP plugin. The tunnel is the transport; the server still needs narrow read-only operations. Do not reuse or bypass the earlier refused network-bridge source write. Use only a reviewed, restricted server and sanitized projections once independently admitted. No live endpoints, credential values or sovereign infrastructure details belong in this public workcell.

**Local readiness distinction:** Native UIA and consented first-party metadata capture are available for local testing; ChatGPT-facing MCP connection remains unqualified and should not be presented as working.

## Reuse existing authoritative native summary validator first

The repository already ships `tools/local/Test-NativeMetadataSummary.ps1` and `tools/local/Test-SupraChatWinAppLifecycle.ps1`; do **not** replace them with a weaker second native acceptance test. The first validator checks the source schema, assembly hash, detailed lifecycle status, recorded/retained/evicted event accounting, diagnostic totals and bounded history. The WinApp lifecycle harness can independently exercise Stop, Expiry, Navigation, ReviewClose, and OwnerClose under explicit local admission. Neither belongs in public CI with a signed-in profile.

For an operator-selected saved summary and its **matching reviewed application binary** (Windows PowerShell is sufficient for this command block, but the native validator itself requires PowerShell 7 / `pwsh` and .NET System.Text.Json):

```powershell
$summary = 'C:\ABSOLUTE\PATH\TO\suprachat-metadata-summary-EXAMPLE.json'
$appDll  = 'C:\ABSOLUTE\PATH\TO\SupraChat.dll'
$receipt = 'C:\ABSOLUTE\PRIVATE\FOLDER\native-validation.json'
$sha = (Get-FileHash -LiteralPath $appDll -Algorithm SHA256).Hash
pwsh -NoProfile -File .\tools\local\Test-NativeMetadataSummary.ps1 -SummaryPath $summary -ExpectedAssemblyHash $sha -ExpectedEndReason SummarySave -ReceiptPath $receipt
```

Use the actual `ExpectedEndReason` for the reviewed test and a **new** receipt file; no overwrite is permitted. Stop if the build hash, lifecycle, accounting, or privacy check fails. Once the native validator passes, run the independent `export_native_summary.py` projection against that same immutable saved source file. The exporter never uploads or exposes the original native summary.

Public `qualify-oec-native-aggregate-prior-art.yml` now exercises the original validator's synthetic self-test in addition to the 34 Python negatives on Ubuntu and Windows; it does **not** run the actual first-party consent flow. The first signed-in UI test, tunnel admission, and ChatGPT-side custom MCP connection remain independently held.
