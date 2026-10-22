# Shared multi-session cooperation — synthetic RDTE only

This source branch is a **bounded, unmerged experimental slice** of the revised
human/accessibility + automation + agent collaboration requirement in SupraChat #19.
It starts from public qualified W2 combined PR #31; it does not change or merge
that work, private user-script bridges, credential owners, terminals or Agent Dispatch.

## What the code actually does

- A standalone, transport-independent `net10.0` state-machine library holds
  up to 16 synthetic sessions; each has its **own** stable session ID,
  participant registry, revision, event history and operation-id receipts.
- A headless long-lived .NET process exposes the **same** registry to different
  local subprocess clients through user-only named pipes. There is no
  client-global or UI-global active-session selection.
- Participants are tagged `human`, `automation`, or `agent` purely for
  deterministic RDTE. These tags are **not authenticated delegated identities**.
- Concurrent operations on A respect optimistic revision fencing and exact
  idempotency; different payloads with the same operation key fail. A retry by
  another joined A participant may retrieve the previous operation receipt.
  Reads are available from independent client processes.
- A synthetic uncertain-intent event quarantines session A and refuses new
  mutations, without contacting any model or browser. Session B continues.
- Changes stage to an isolated copy and sync to an atomic-replacement local
  snapshot **before replying**. Host restart preserves the operation ledger.
  A store-level exclusive lock prevents simultaneous local owner processes.
- A GHA runner starts a real host subprocess plus separate clients on Windows,
  Linux and macOS, testing concurrency, duplicate handling, cross-client read,
  isolation, quarantine, restart and replay.

## Limits that must not be disguised

- The only implementation is a **synthetic local metadata coordinator**.
  It is **not** a ChatGPT conversation controller and provides no provider
  calls, prompt send, terminal execution, credential management, remote access,
  browser bridge or GUI integration. No transcripts are stored.
- Local pipe CurrentUserOnly and process-level file exclusivity do not
  authenticate human/automation/agent identities; actor names are self-asserted.
  Never expose outside a trusted local-user boundary or store private content.
- File replacement plus fsync covers ordinary process-restart tests, **not**
  formally proven power-loss crash consistency or corruption recovery.
  No cross-host lease, replication, distributed consensus or HA.
- Events are polled with a revision cursor, not streamed subscriptions.
  Histories have a hard 256-event cap to avoid silently losing dedup receipts;
  retention/compaction needs separate qualification.
- Metadata writes serialize briefly for durable state consistency.
  Future long provider calls must never hold the shared metadata lock.
- `NOTE` is metadata only. `UNCERTAIN_INTENT_SIMULATION` is a causal
  failure fixture, not a real network submission.
- The test CLI is a transport tool, not production human UX, accessibility
  support, or a mandatory end-user CLI product.

## Follow-on integration / acceptance gates

1. Independently review exact-head three-OS CI, including session-specific
   state, cap behavior, crash recovery, forged actor roles, local pipe access,
   file locking, no provider calls and no credential exposure.
2. Connect the accepted session core to one accessible human GUI and two
   automation/agent participant surfaces through the **same shared host**.
   Implement status/event subscriptions, catch-up cursors and accessible cues.
3. Add real delegated actor authority and session/resource-scoped policies;
   adopt provider adapters separately without equating ChatGPT.com
   conversation IDs with SIWC/Responses or Codex threads.
4. Reuse independently qualified private ChatGPT loopback bridge after
   admission; prove the same actual approved turn visible to all participants,
   while a second live session progresses independently.
5. Test duplicate submissions, provider lost-ACK, foreign UI changes,
   source/worktree collisions, client restart, accessibility HIL and
   host-failure recovery. Keep the human out of relay/scheduler duties.
6. Owner review, security verification, merge, deployment and physical NAS
   workload gates remain distinct from synthetic CI.

## Reproduction

```sh
dotnet build SupraChat.SessionHost/SupraChat.SessionHost.csproj -c Release
python tools/qualify-shared-session-host.py SupraChat.SessionHost/bin/Release/net10.0/SupraChat.SessionHost.dll
```

The test uses only public synthetic fixtures. There is **no** need to
install a container daemon on the Windows development machine and no
authenticated ChatGPT account is contacted.

## Second bounded increment — shared human and machine projections

The current COW branch now adds:

- A reusable `SharedSessionClient` in the session core. The existing Avalonia
  Sessions tab has a **separate, explicitly labeled synthetic shared-host
  panel**. A human participant can enter the local named-pipe identifier,
  refresh/inspect shared session identity and ordered events, create a session,
  join as a self-asserted human, and append a revision-fenced metadata note.
  Accessible names, automation IDs and status announcements accompany controls.
  Local W1/W2 workbench state is **not silently migrated or promoted**.
- Existing `SupraChat.Automation stdio` JSON-RPC exposes
  `session/shared/call` with `{"pipe":"...","request":{"action":"session/list"}}`
  and other typed registry methods, all using the **same** named-pipe client.
  This is machine/agent access via an existing surface, **not** an MCP
  implementation or a second independently authoritative session store.
- Public-safe qualification starts the actual shared host, uses multiple
  independent client processes, and separately invokes the established
  automation JSON-RPC process to observe and join the same sessions.
  Windows/Linux/macOS build the real Avalonia projection and check semantic
  automation IDs. Full application Windows UI Automation must also locate the
  new shared control peers before this branch is promoted.

### Reproduce without credentials or a browser

From repository root, in a terminal you control, with a disposable absolute
store path and selected user-only pipe name:

```sh
dotnet run --project SupraChat.SessionHost -- serve --pipe suprachat_rdte_local /absolute/path/to/disposable-session-metadata.json
```

Open the branch-built SupraChat GUI. In **Sessions -> Shared multi-session
collaboration (synthetic local RDTE)**, use pipe `suprachat_rdte_local` and
explicitly connect. A separate machine client in the same OS user context can
call the host through `SupraChat.Automation stdio`:

```json
{"jsonrpc":"2.0","id":1,"method":"session/shared/call","params":{"pipe":"suprachat_rdte_local","request":{"action":"session/list"}}}
```

The directly callable host CLI is retained for deterministic automation tests;
these interfaces all call the same registry. Neither the GUI nor automation
starts a hidden local daemon or external ChatGPT action.

**Safety limits:** User-provided titles, note labels, participant IDs and
synthetic metadata are currently written in plaintext at the user-selected
store path. Use only disposable test labels. Host pipe access is restricted to
the same local OS user but cannot distinguish an authorized human from a
malicious process owned by that user; actor roles are self-asserted.
No remote exposure, cryptographic identity, credential storage, real provider
turn, model inference, terminal invocation or live collaboration approval
is established. The full production GUI must replace its local in-memory
workbench source of truth only after explicit migration semantics and testing.

### Additional red-team and acceptance gates

- Same-session UI and agent consumer identities, revisions and history must be
  observed in one causal local session rep, including accessible controls.
- Native Windows UIA smoke validates semantic control peers, but real
  keyboard-only and NVDA/VoiceOver usability remain independent HIL gates.
- Add participant capability admission, robust durable transaction recovery,
  server push/event cursor subscriptions, and per-conversation provider
  arbitration before any live ChatGPT session access or remote sharing.
