# S0 Session Authority — concrete implementation design and adversarial qualification (candidate)

Status: **DESIGN CANDIDATE / NOT IMPLEMENTED / NOT RELEASE QUALIFIED**  
Product owner: [SupraChat issue #19](https://github.com/SemperSupra/suprachat/issues/19), [private DLE #2](https://github.com/SemperSupra/suprachat-private/issues/2)  
Portfolio reference: engineering-governance candidate ADR-0003 (Ports and Adapters; optional federation).  
Design baseline: public `main@a0fa56c943d73482ac233834b96f31fb591fd385`, W1 release draft #111 `6316eae8280ef91ada6f64779c5864def7e183b5`.  
Last source verification: 2026-10-31. Recheck heads, CI, worktrees and grants before source changes.

## Decision and non-goals

**Decision:** One **unprivileged, per-user, per-profile local Session Authority process** owns all mutable *SupraChat workbench session metadata* and one recoverable ledger. Independently launched GUI, CLI and agent JSON-RPC clients connect through the same narrow local OS IPC service and use one versioned use-case contract. Human-facing Avalonia `WorkbenchView` is a **driving adapter**, not a second ledger. Derived `LedgerWorkbenchProjection` is a **read model**, not the authority.

No cloud proxy, NATS, new global registry, replicated ledger, provider-conversation mirror, automatic ChatGPT Web control, credential migration, remote actuator, AI inference service, terminal execute or generic tool execution is part of S0. Existing browser/WebView and native provider/Codex authority stay in their owning processes. One workbench session may contain *references* to native provider threads; these are not interchangeable identifiers.

The **earliest acceptance slice** is one real owned process, one real GUI window, one separately invoked automation CLI and one separately invoked agent stdio process observing and (when granted) changing the same session ID/version with no shadow state. Durability, per-OS peer/issuer authority and adversarial crash tests complete before v0.0.1 release consideration.

## Existing implementation to compose, not rewrite

| Source (already public) | Adopt | Preserve exclusions |
| --- | --- | --- |
| `SupraChat/Core/SharedSessionLedger.cs` + `SharedSessionAdmission.cs` | One locked state-transition implementation, CAS, immutable first outcome, authoritatively scoped command and revoked-grant recheck. | Not an independent process authority, not a real user/peer authenticator. |
| `SupraChat.Automation/SessionLedgerHost.cs` + `SessionLedgerClient.cs` at W6 PR #37 | Fixed operation routes, JSON outcomes, independently exercised CLI/agent clients, `tools/qualify-w6-shared-ledger.mjs`. | Loopback bearer is NOT authenticated actor identity; avoid production-bearer-only writes. |
| `SupraChat.PeerLedgerRdte/SharedDesktopHost.cs` + `SharedDesktopLab.cs` on public main | **Most advanced native integration oracle**: one ledger, accessible real Avalonia window, named-pipe automation and JSON-RPC agent child, scoped admission, CAS, grant revocation, recovery. | It is a disposable parent-launched fixture with fixed sessions and pre-enrolled children, **not** arbitrary external CLI/agent attachment nor normal WorkbenchView. Keep lab intact. |
| `SupraChat.PeerLedgerRdte/Program.cs` peer checks and `WindowsOwnerCustody.cs` | CurrentUserOnly named pipe, observed client PID/start time/SID, protected Windows root and issuer research. | Production OS identity and hostile same-user/multi-user/path race resistance not qualified; DPAPI CurrentUser is not per-process trust. |
| `SupraChat.PeerLedgerRdte/W9DurableSessionStore.cs` + `W12ScopedGrantAuthority.cs` | One-store, FULL-WAL atomic snapshot/first-outcome, owner lock, synthetic scoped grants and revocation checks. | Disposable synthetic env-gated storage/key custody, no silent promotion to production. |
| `SupraChat/Core/LedgerWorkbenchProjection.cs` + W1 `WorkbenchView.axaml.cs` | Current accessible, metadata-only UI and projection semantics. | W1's `new SharedSessionLedger()` is precisely the release blocker. W1 capacity 16 vs ledger 32 must be explicit. |
| W14/W15 event cursor/host incarnation; OpenCode event log; Codex App Server | Bounded event replay and gap/epoch defense; connection/session identity separation, slow-client isolation. | Do not import alternate MainWindow owner or Rust/TS runtime. |

Additional independent source comparison: Codex App Server `thread_state.rs`, `transport.rs`, `app-server-transport/.../unix_socket.rs` (client/thread identity, bounded output, private socket); OpenAI Agents JS `SessionHistoryTransactionAwareSession` (stable operation ID, reject changed payload/expected-suffix); Goose confirmation coordinator (one valid approval); OpenHands conversation lease (later S2–S4 only).

## Runtime decomposition

```text
 Native GUI             Automation CLI           Agent stdio JSON-RPC
 (own drafts/view)      (typed receipt)           (typed result)
      |                       |                         |
      +-----------------------+-------------------------+
                 SessionAuthorityClient
                     local OS IPC
                 (no generic HTTP listener)
                            |
              SessionAuthorityHost process
          lifecycle + transport + authenticated peers
          fixed dispatcher + authority/grant admission
                      SharedSessionLedger
                            |
                     W9-style SQLite
          one writer, checkpoint + outcomes + revocations
```

**Packaging decision:** Initially rehost existing product/automation executable with a new explicit `session-authority serve` mode rather than introducing a privileged Windows Service or daemon installer. It runs under the current logged-in user, independently of GUI lifetime. Startup must acquire an exclusive per-profile owner lock **before** advertising endpoint. GUI may explicitly start an absent owner during user-initiated startup; CLI/agent must never silently instantiate a competing owner. An unavailable host returns `OWNER_UNAVAILABLE`, never falls back to a process-local ledger. No background service installation, elevation or remote binding.

`SharedDesktopLab` remains the red/blue oracle: product code is extracted by small reviewed changes; do not turn the fixture itself into a production service. Reuse current `SupraChat.Automation` for hosting and existing `stdio` bridge for agent calls. The current `SupraChat.Automation.csproj` references the whole desktop project; a new shared core class library is **optional future refactoring** if build/startup overhead merits it, not S0 prerequisite.

### Process and identity boundaries

- `profile_id` (stable per user/product profile) namespaces a single local authority and its data directory; `owner_incarnation` (fresh random per boot) identifies the running process; stable `session_id` (UUID) identifies the workbench session. Provider thread IDs are references with provider type, not aliases.
- **Windows:** existing `NamedPipeServerStream` / `PipeOptions.CurrentUserOnly` plus observed SID/PID/start time and explicit enrolled peer policy. OS pipe authorization alone identifies a user, **not an approved human/agent role**.
- **Linux/macOS:** private per-user directory and Unix-domain socket mode with actual peer credential validation. .NET 10 Unix named-pipe node permissions can inherit umask; test explicit mode/path/parent ownership, not .NET 11 assumptions.
- Transport establishes a peer subject. Capability admission separately authorizes specific `session_id` + command scope + expiry, revocation + actor identity; no `audience` claim in the JSON body creates privilege. Default agent/automation unpaired or ungranted connections are denied, including reads unless explicitly authorized by product policy. A human UI action must not be inferred solely from the same user SID or a GUI label.
- **Bootstrap/open problem to qualify:** production trusted first GUI enrollment and later arbitrary external CLI/agent pairing (no bearer environment secrets, signing key on agent, public mint endpoint or hardcoded human trust). Use existing controlled child-enrollment for synthetic proof; propose a local user-reviewed pairing transaction with one-use capability only after actual issuer/storage/security review. This is the critical security seam, not solved by documentation.
- A compromised process running as the same OS user may defeat per-user store/DPAPI isolation. Define and review this threat boundary honestly; native different-user/restricted-token tests are necessary but not a proof against same-user compromise.

## Frozen candidate v1 use-case contract (no raw host or browser access)

`session/hello` (version, incarnation, capabilities/permissions), `session/list`, `session/read`, `session/create`, `session/change` (title/conversation reference/workspace key metadata only), `session/events` (bounded after-cursor read), `session/operation-status` (known receipt lookup), and `session/grant/revoke-self`. No arbitrary method dispatch, generic URL fetch, model prompt, terminal execute, transcript export, grant issuance, or remote proxy under this namespace.

**Candidate request envelope (illustrative; adapt to existing W6/W8 parsing where possible):**

```json
{
  "schema": "suprachat.session.command/v1",
  "operation_id": "b0d99f2fc8cc4aa8955fa6e0ab640b97",
  "owner_incarnation": "64c4e4d21d574299a38572e68d87fbcc",
  "session_id": "c3b2334bd21b4bc093f3ab95f1a0b8e6",
  "expected_version": 3,
  "method": "session/change",
  "params": {"operation": "set-title", "value": "Synthetic session title"}
}
```

**Candidate response envelope:**

```json
{
  "schema": "suprachat.session.receipt/v1",
  "operation_id": "b0d99f2fc8cc4aa8955fa6e0ab640b97",
  "status": "APPLIED",
  "replayed": false,
  "owner_incarnation": "64c4e4d21d574299a38572e68d87fbcc",
  "session_id": "c3b2334bd21b4bc093f3ab95f1a0b8e6",
  "session_version": 4,
  "event_sequence": 42
}
```

The established JSON-RPC `id` is **request-correlation only**; `operation_id` is the stable durable idempotency key and must survive a client reconnect. The host derives actor/grant identity from trusted authenticated connection context, **not** from the envelope. It computes a canonical payload fingerprint and scopes operation identities to verified actor/authority domain (do not scope only to a rotating grant if historical legitimate replay must survive renewal). `owner_incarnation` is a connection-staleness fence, not a user credential.

**Response codes:** `APPLIED`, `OBSERVED`, `REPLAY`, `VERSION_CONFLICT`, `IDEMPOTENCY_KEY_PAYLOAD_CONFLICT`, `ADMISSION_REFUSED`, `GRANT_REVOKED`, `SESSION_NOT_FOUND`, `CAPACITY_REACHED`, `OWNER_UNAVAILABLE`, `OWNER_CHANGED_RECONNECT`, `EVENT_GAP_RESNAPSHOT_REQUIRED`, `OUTCOME_UNKNOWN`, `STORAGE_UNCERTAIN_OWNER_STOPPED`. Wire protocol version errors fail closed. Preserve internal W6 result semantics behind the versioned adapter instead of mass-renaming existing core outcomes.

**Capacity:** Externally admit at most **16 simultaneous workbench sessions** until W1 projection is explicitly changed and requalified (core presently allows 32); refuse the 17th from **every** adapter, no 17th hidden from GUI. Preserve the bounded 1024 event and 4096 idempotency record invariants; when full, refuse *new* mutations rather than silently discarding replay data. A later retention/compaction design is separate.

### Authoritative mutation and observation

1. Client negotiates version/current host incarnation, receives explicit capabilities and confirms local owner; request is length-/time-bounded.
2. Authenticate OS peer *and* validate enrolled/scope grant. Observe/identify peer before admission; bind connection's immutable observed identity/instance so caller cannot choose role. Recheck relevant revocation, session scope, operation ID, and expected version at the **commit gate**.
3. Under one existing `SharedSessionLedger` mutation gate: reject replay-conflict/stale version, apply one permitted metadata change, assign exactly one sequence, persist the state + immutable first result + any durable revocation together **before** ACK.
4. On checkpoint uncertainty, fail-stop the owner (existing W9 property); do not serve advanced but non-durable state. On lost ACK, the client **retries the same `operation_id` + canonical payload**, queries operation-status, or reports `OUTCOME_UNKNOWN`, never invents a new logical command ID.
5. GUI can initially use explicit refresh after every change and bounded after-cursor polling. Next use-case is `events(after, incarnation)` replay with `EVENT_GAP_RESNAPSHOT_REQUIRED` on retention gap, process epoch change, or rewind. Do not add a dedicated message broker for S0.
6. Separate connection/per-client request queues and cancellation from underlying session. Bound outstanding requests and response sizes; a stalled agent subscriber must not hold the ledger lock or the UI responsiveness hostage. Following an event stream never implies permission to mutate.

**Safety invariants to model/test:** one owner per profile; one committed effect per verified actor+operation+fingerprint; no result before durable commit; grant revoked at effect boundary cannot mutate/replay; CAS version monotone; identity and incarnation mismatch fail closed; no lost/duplicate session sequence on reconnect; projections never mutate authority. Cancellation of client transport does **not** imply rolled-back mutation.

## Implementation batches and dependencies

| Batch | Source delta | Can run in parallel? | Acceptance gate |
| --- | --- | --- | --- |
| D0 — Contract/source map | Compare exact W1, W6, W9, W12, #51 fixture and existing CLI/stdio code; freeze field names, ownership, error mapping and session capacity. | **First, sequential** | Source-selection matrix and owner/security sign-off on proposed trust bootstrap. |
| A — Session host | Extract fixture's OS-peer/ledger/SQLite behavior behind fixed handler, one owner lock, version/epoch-aware startup/teardown; no real provider actions. | Yes after D0 | Two independent clients share one persistent ledger; owner unavailable never shadows. |
| B — GUI adapter | Replace `WorkbenchView.axaml.cs:10 new SharedSessionLedger()`; keep local selection/drafts, bind read model, create/rename via host; preserve accessibility IDs. | Yes after D0 | Native GUI creates, refreshes, and reacts to CAS conflict. |
| C — Machine adapters | Adapt W6 CLI `session-ledger-*` and existing agent `stdio` to one shared client + typed semantics; no GUI scraping. | Yes after D0 | Independently spawned automation+agent clients act on same ID/version. |
| D — Identity/custody | Separate peer identity and principal capabilities; qualify Windows pipe and Unix permissions; promote W14 custody and W12 issuer only with explicit security review. | Yes after D0 | forged role, same-user unauthorized child, different-user negative, stale/revoked grant refuse. |
| E — Independent tests | Extend `Test-SharedDesktopLab.ps1`, W6/W9/W13 fixtures and W1 strict Windows UIA; synthetic loss/crash/reconnect/slow-client oracles. | Yes after D0 | Three-platform public-safe tests, native Windows HIL, fault proofs with first-failure receipt. |
| I — Integration | Reconcile A–E into isolated W1 candidate; run exact-head native build, tests, adversarial review, release-gate check. | **Sequential after A–E** | `S0_SHARED_AUTHORITY=PASS` only on actual installed/process-separated product. |

Do not overwrite the independent W2 #112–#114 draft stack or release workflow #111. Public #51 already owns prior synthetic shared-desktop fixture; narrow real-product change in #19 with #51 cross-reference. Maintain workcell locks and owner gates. Use $0 public GHA with only public/sanitized synthetic code; real local Windows HIL in its admitted environment, no private Actions minutes.

## Red-team / blue-team review (blocking unless risk accepted)

| Attack / first failure | Impact | Blue-team smallest control / proof |
| --- | --- | --- |
| GUI silently creates a local ledger after daemon disconnect | split-brain: misleading same-session UI | remove direct W1 writable ledger field, no fallback; `OWNER_UNAVAILABLE` with explicit recovery UI |
| Fixture-only enrollment masquerades as production arbitrary client auth | agent privilege spoof / fake human approval | real OS peer enrollment + explicitly reviewed scoped grants; red/blue arbitrary-client native HIL before promotion |
| Same OS UID but unapproved process connects or reads store | unintended access | per-process scope, connection-binding, owner-only root, no auto-auth from SID; threat-limit for compromised same-user apps |
| `operation_id` regenerated on retry or changed payload reused | double commit or confused result | fixed ID/payload digest/actor namespace, immutable outcome journal; retry/lost ACK oracles |
| Crash between memory mutation, WAL checkpoint, ACK | stale or phantom state | committed checkpoint+receipt in same transaction; fail-stop on uncertainty; restart old result replay |
| W9 2 MB whole-ledger checkpoint grows or 4096 dedup records exhaust | latency/capacity denial | bounded performance/capacity benchmark, honest `CAPACITY_REACHED`, separate normalized-SQL design only if needed |
| Windows pipe/POSIX UDS path spoof; inherited permissive umask | cross-principal write | actual kernel peer identity, unique protected rendezvous, enforce 0600/0700 on Unix and deny other-user attach; no DNS/loopback fallback |
| Agent responds to confirmation twice or wrong session | duplicated approved effect | request-specific one-use confirmation state and CAS; provider actions not S0 |
| Old event cursor is treated as continuous after process reboot | false synchronization | owner incarnation + sequence cursor, explicit gap/resnapshot; never reuse silently |
| Slow or hostile client holds global mutation lock | availability denial | bounded input/output/deadlines, per-client queue, drop stalled observer, no external callbacks while ledger lock held |
| Full W6/old MainWindow branch merged wholesale over W1/W2 | regression or overwritten actor work | code-level selective extraction + independent COW worktrees; exact merged head qualification |
| Source/unit tests falsely certify usable product | release without three-audience parity | actual product GUI UIA + independent CLI/stdio processes + crash/unauthorized negatives; no static substitute |
| New background process becomes SIWC/credential owner | credential spill/unauthorized provider effect | metadata-only host, no WebView/cookies/tokens/provider execution, no provider RPC/terminal method |

**Red-team disposition:** Biggest design risks are (a) trusted arbitrary-client enrollment, (b) production SQLite issuer/store custody and OS native security, (c) integrating #51 fixture semantics with *current* W1/W2 without parallel authorities. These are **release blockers**, not solved just because synthetic CI succeeded.

**Blue-team disposition:** The design is efficient if it keeps existing W6 ledger, #51 peer and desktop fixture, W9 checkpoint and W1 accessibility surface; introduces only a fixed IPC command adapter + scoped host lifecycle and real-client pairing; and declines any remote/federated features until S0 is independently accepted.

## Native v0.0.1 qualification (must prove, not infer)

1. Start one owned service and GUI; create session S via GUI. Independently launched CLI and agent stdio inspect identical `profile_id + session_id + version + sequence`.
2. Authorized automation changes title S at expected version. GUI/agent see same new version without restarting; wrong-version human/agent changes refuse.
3. Agent changes S with granted scope; forged role, unpaired process, wrong session, revoked grant and stale incarnation refuse; another permitted session T remains independent.
4. Duplicate operation ID + same canonical request returns exact replay; same key with changed content conflicts; lost ACK/cancel-after-commit cannot create second effect.
5. Kill/restart owner at before-commit and after-commit-before-ACK points. All acknowledged metadata/replay/revocation survives; no implicit empty genesis, second owner or UID-crossing store.
6. Subscription/poll from a captured cursor sees each event in order; gap and host reboot require re-snapshot; slow client cannot block GUI.
7. Windows native UIA identifies and actuates real W1 accessible controls; CLI, stdio and GUI verified in separate real processes; Linux/macOS local transports/permissions and contracts pass when support is claimed.
8. No provider call, chat send, browser cookie/token, transcript, unreviewed transfer draft or terminal command appears in host output, logs, CI or durable store. Receipts redact titles, paths, process/subject IDs and security tokens.
9. Exact-head tri-OS CI + independent source/security review + real native HIL PASS; product owner's existing legal, notice, security, branch/merge, packaging and release holds remain separate.

## Deployment, rollback and continuation

**D0/D1 synthetic:** separate isolated workcell, no real user data, explicit manual host launch; CLI and agent read-only until scoped grants qualified.  
**D2 local disposable integrated:** one per-user authority under owned temporary profile, actual Avalonia/CLI/stdio attachments, W9-style checkpoint, OS-peer test; no SIWC/authenticated live provider usage.  
**D3 release-profile admission:** reviewed permissions, issuer/key lifecycle, actual user-profile backup/recovery, installer and cleanup, same-user risk acceptance, independent native tests; rollback via old binary + preserved *unmodified* store backup / schema-compatible read-only verification. **No silent downgrade/migrate/empty reset**.  
**D4 owner release decision:** only once S0 evidence plus all existing release gates PASS.  
**Post-v0.0.1:** local proxy; two same-host instances (explicit instance namespaces); OCI tunnel/MCP gateway; multi-host routing/fencing. No remote/deployment authority is inferred from S0.

**First local actor baton:** (1) read current PR heads + local worktree census, (2) compare `SharedDesktopHost` / `SharedDesktopLab` and W6/W9 with W1 `WorkbenchView` and `Automation.Program`, (3) write P0 interface/field/error mapping and identify how arbitrary external clients can be safely enrolled, (4) implement the smallest separate-process synthetic real-WorkbenchView-to-host slice on an admitted isolated branch, (5) independently verify and return exact first-failure and next action to private DLE #2. No unqualified merge, release, secrets, paid execution or new scheduler.

**Complexity kill test:** If local W1 UI + W6 host can share one owner using fewer and safer changes than extracting #51 fixture, retain existing W6 host for synthetic read-only demonstration and adopt only #51's security/test semantics. Do not prematurely create an extensible daemon framework, additional project/registry, or durable data format unproven by consumer tests.
