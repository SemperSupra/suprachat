# Owned transport metadata capture (issue #51, T2)

This candidate stacks on T0 trace-core PR #54. It does not compose the unmerged shared-session,
W8 identity, browser observer or durable-storage prototypes. The native WebView2 probe is separate
PR #53 and has an unresolved streamed-response terminal-event discrepancy.

## Transport boundaries

Responses HTTP/SSE, Responses WebSocket and Codex app-server stdio accept an optional
OwnedTransportTrace binding. The desktop and machine shells bind these transports to one
LocalTraceController per window/process. Capture starts OFF. No browser or credential-store traffic
is instrumented. Model discovery, OAuth requests and Codex stderr remain outside trace capture.

When OFF, the binding does not parse provider JSON. When enabled, it emits only typed allowlisted
metadata, byte counts and normalized event kinds into the same bounded suprachat-trace/v1 core.
No prompts, deltas, headers, method names, raw provider event names or exception messages enter
trace events. SSE correlations use a fresh local request identifier; WebSocket frames use a fresh
connection identifier; JSON-RPC request/response IDs are pseudonymized separately from the
connection identifier. IDs are never inferred from a ChatGPT URL or conversation.

The HTTP attempt is REQUESTED before SendAsync. Received headers/events and successfully written
WebSocket/stdio frames are OBSERVED. A protocol response.completed event or socket closure is an
observation, not a VERIFIED provider operation. EOF without response.completed marks a capture gap.
Transport cancellation is not proof that remote compute stopped. Existing raw provider callbacks
and returned content remain separate, unchanged product interfaces.

Recorder errors increment a metadata-only RecorderFailures counter and do not change a transport's
result, exception, framing or cancellation. Every controller projection includes that counter, capture
scope and gap status. Trace storage remains independent of provider/session authority.

## Three local operator surfaces

The desktop Automation and agents work area includes accessible Start metadata capture, Stop capture,
Read trace and Show export JSON controls, a polite live status and read-only sanitized JSON. Export
shows JSON in the window; it does not automatically save a file. Reads are snapshots refreshed by
the operator, not a second unbounded event subscription.

Automation has `trace-status` and a persistent `trace-session` JSON-line command loop. This avoids
pretending a start command in a process that immediately exits can enable another process's capture.
For example, pipe these lines to `suprachat-cli trace-session`:

```json
{"operation":"status"}
{"operation":"start"}
{"operation":"read","params":{"after_sequence":0,"limit":25}}
{"operation":"stop"}
{"operation":"export"}
```

Within the same loop, `respond`, `responses-raw` and `codex-rpc` operations accept an `arguments`
array and directly use the existing CLI operation handlers. `responses-raw` must supply --body so
it cannot consume the remaining command stream. Those provider operations keep their existing
authorization requirements and raw product results. Only the trace management responses promise
metadata-only output. No provider action is automatically performed by starting capture.

Agents use the existing persistent stdio JSON-RPC transport with trace/status, trace/start, trace/stop,
trace/read and trace/export. Optional params are session_id, and for reads/exports after_sequence and
limit. A supplied session UUID must match the current process. Unsupported parameters, claimed
actor principals, payload modes and unknown trace operations are refused. Omitted session_id selects
the single local controller capability; it does not discover or attach to another window/process.

All three surfaces call LocalTraceController.Execute and receive the same JSON schema and states.
Metadata pages use T0 pagination, retention/eviction counters, completeness UNKNOWN and explicit
capture_gap. Stop keeps retained observations readable and discloses the unobserved interval.

This is a **single-operator local experiment**. A local parent process already controls its stdio child.
Process-local capability ownership is not authenticated W8 peer identity or a cross-actor grant. There
is no network trace endpoint, shared host inventory, cross-process read, payload vault or durable trace
store here. Shared-session composition and privileged export/control remain gated by the chosen
session authority and W8 proof. Browser observation and first-party actuation remain unavailable.

## Qualification

```powershell
dotnet run --project SupraChat.ContractTests/SupraChat.ContractTests.csproj -c Release
dotnet build SupraChat.Automation/SupraChat.Automation.csproj -c Release
./tools/local/Test-OwnedTraceMachineControls.ps1
```

Synthetic fixtures exercise the actual owned boundaries: a fake HttpMessageHandler with SSE,
fragmented WebSocket frames split inside a UTF-8 character, and a real persistent redirected .NET
subprocess speaking JSON-RPC. They check unmodified raw results, OFF/start/stop/read/export behavior,
request/response aliases, HTTP error/incomplete/cancel paths, secret exclusion and recorder failure
independence. Controller tests reject wrong sessions and forged authority claims. No runtime credentials,
Codex binary, provider network access or paid inference are used by these tests.

The public tri-OS workflow repeats core/transport fixtures and machine projection checks. Windows
UIA additionally requires every trace control, invokes start/stop, and reads the output ValuePattern
to verify METADATA/OFF state changes. Static UI metadata and existing product qualification remain
required. A green synthetic run does not establish authenticated provider behavior or live browser
observation. Next: independent review; T3 synthetic actuator policy/negative cases, with live first-party
modification still denied; T4 only after the identity/storage/adapter gates in issue #51.
