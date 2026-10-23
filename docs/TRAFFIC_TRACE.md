# Traffic trace foundation (issue #51, T0)

This candidate adds a metadata-only shared core for the ChatGPT integration and control plan in
[issue #51](https://github.com/SemperSupra/suprachat/issues/51), based on main
`250e5ef9d2a3017e8374d1a8243429abcc54eaf0`. Qualification is **SYNTHETIC**.
It does not attach to ChatGPT, WebView2, Responses or Codex transports yet. Existing product
behavior and dogfood diagnostics are unchanged.

`TraceNormalizer` implements `ITraceSink`. `ITraceSource` supplies typed observations via an async
enumerable; `PumpAsync` runs a source, records its observations, and marks failures/cancellation as
capture gaps. This is a separate bounded observation store, never a durable session mutation ledger.

## Capture and local ownership

`CreateSession()` returns an in-memory `LocalTraceSession` capability with capture initially OFF.
`SetCapture(capability, Metadata)` explicitly enables recording. Stop switches to OFF; a later restart
reports the unobserved interval. Closing a session clears its retained events and invalidates its
capability. A capability from another normalizer cannot access or change this normalizer's state.

This capability is only for a single-operator local experiment. It is not a wire credential or an
authenticated W8 actor identity. There is no remote endpoint, GUI control, CLI or JSON-RPC route in
T0. All future audience projections must use this same semantic core and authenticate/authorize
session access before returning observations. A session UUID alone grants nothing.

`TracePolicy` allows metadata start/stop/read/export at the operation-class level. Actual core methods
also require the local capability. Privileged payload capture, provider cancellation and first-party
modification are denied. The payload vault interface is an unimplemented boundary for later reviewed
encrypted storage; no payload is accepted by the normalizer, persisted, or included in its events.

## Wire contract: suprachat-trace/v1

Serialize immutable events/pages using `TraceNormalizer.JsonOptions` (snake_case properties, uppercase
snake_case string enums). Each event carries:

| Field | Meaning |
| --- | --- |
| schema_version | `suprachat-trace/v1` |
| event_id, host_run_id | Random observation and normalizer-run UUIDs; no stable host identity |
| global_sequence | Strict increasing host-run observation order across sessions |
| session_id | Locally generated trace session UUID, not a ChatGPT conversation identifier |
| provider_surface, adapter | Typed surface and adapter labels; adapter-reported evidence |
| observed_origin | UNKNOWN, CHAT_GPT, OPEN_AI_API or LOOPBACK; no raw URI retained |
| direction, event_kind | Typed direction and lifecycle metadata |
| wall_time_utc, monotonic_offset_milliseconds | UTC observation time and elapsed monotonic time |
| request_id, stream_id | Optional HMAC-SHA256 aliases scoped to run, session and identifier domain |
| source_sequence | Optional adapter-reported sequence; duplicates/out-of-order events preserved |
| byte_count, status, latency_milliseconds | Nonnegative sizes/latency and optional HTTP status |
| outcome | Adapter-reported state; default OBSERVED; owned HTTP attempts use REQUESTED; does not establish provider actuation |
| capture_gap | Source gap, rejected metadata, or resumed capture interval |
| payload_classification, redaction_state | METADATA_ONLY, ALLOWLISTED_PSEUDONYMIZED |
| completeness, confidence | UNKNOWN, ADAPTER_REPORTED |

Correlation uses explicit adapter request/stream identifiers only. Paths, query strings, URL fragments,
userinfo, arbitrary hostnames, headers, bodies and provider error messages never enter the event.
Authenticated principal, operation/idempotency key and encrypted payload reference fields are deferred
until their authorities exist; T0 never invents them. The event kind is normalized metadata, not a
raw provider event name or arbitrary string. The adapter must not encode secrets into numeric metadata.

The normalizer aliases identifiers with an ephemeral random HMAC key, cleared on disposal. Aliases
correlate within one run/session/domain only. They cannot be joined across sessions or restarts.
Origin labels recognize exact official HTTPS hosts on the default port; spoofed hosts and URLs
containing userinfo are UNKNOWN. Loopback is a label, not an admission grant.

## Bounds and completeness

Defaults are 16 sessions and 256 events per session. Hard constructor bounds are 64 sessions and 4096
events per session. There are no provider-ID dictionaries that grow with traffic, background queues,
subscriber callbacks, or disk writes. Each ring evicts its oldest observation independently. Read pages
are capped at 1000 events. Locks serialize sequence assignment and ring/page access.

`Read(capability, afterSequence, limit)` returns an immutable snapshot with a continuation cursor,
capture mode, cumulative eviction count and last evicted global sequence. Its capture_gap reports a
cursor behind eviction, a pending source gap, or a gap event in the returned page. The cumulative
eviction count is not the number missing from this particular cursor. A tail reader does not repeatedly
report old evictions. Global sequence jumps caused by another session are not evidence of lost events.

Completeness is always UNKNOWN: absence of ring loss cannot prove that an adapter observed every
network event. Duplicate/out-of-order source events are retained as observations; source sequences
never replace host observation order. An observer crash produces a fixed safe error and pending gap,
without propagating a potentially sensitive exception or inner exception. Consumers must keep the
trace store outside session authority and handle recorder errors separately from provider operations.

## Validation and next work

Run from the repository root:

```powershell
dotnet run --project SupraChat.ContractTests/SupraChat.ContractTests.csproj -c Release
dotnet build SupraChat.Automation/SupraChat.Automation.csproj -c Release
```

The existing public tri-OS qualification workflow runs the contract executable, including the new T0
checks. Tests use only synthetic metadata: typed HTTP/SSE, WebSocket and JSON-RPC surface fixtures,
interleaved and simultaneous sessions, duplicate/out-of-order observations, paginated reads, overflow,
stop/resume, source errors/cancellation, foreign capabilities, invalid enum/size/status inputs, and secret
sentinels in URLs/identifiers/errors. These are core contract fixtures, not native transport qualification.

Next: T1 disposable Windows WebView2 attach/detach and synthetic native network evidence; T2 native
owned-transport adapters and accessible/CLI/agent projections; T3 authenticated actuator contracts.
The [official Responses streaming guide](https://developers.openai.com/api/docs/guides/streaming-responses)
defines typed SSE lifecycle events for the future owned-transport adapter. No live first-party traffic
capture/modification, remote actor authority, store integration, merge or release is qualified here.
