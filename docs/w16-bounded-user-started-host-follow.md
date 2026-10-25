# W16 — explicitly enabled, bounded read-only W6 metadata follower

Authority: SupraChat #19 DLE (Durable, Legible, Executable). Branch stacks
on the exact-head W15 host-epoch fence, without replacing W6's mutation
authority. This is a synthetic localhost observer, **not a ChatGPT product
conversation/turn history synchronization implementation**.

## Actual workflow and bounds

1. A human explicitly clicks **Read shared host inventory**, chooses a
   session by W6 host ID, then clicks **Start bounded host follow**.
   No automatic startup, host discovery, initial browser sign-in, provider
   prompt submit, or remote write. The existing loopback bearer remains
   just a transport credential, not OS principal authentication.
2. The GUI requires an existing selected host GUID, validated per-process
   epoch (W15), explicit nonprivileged port and previously observed global
   sequence. It will not start against a guessed host or a local workbench
   tab. It does **at most 20 GETs**, spaced at least three seconds apart,
   with no parallel or queued requests from its own follower.
3. Shared `SharedSessionInventoryReader.ObserveAsync(port,token,ct)`
   remains a **GET only** to the fixed 127.0.0.1 inventory endpoint,
   no proxies, cookies, redirects or Origin; it retains 64KiB body limit
   and complete four-second timeout. A new caller cancellation token is
   linked to that complete-read deadline so Stop/Close interrupts the
   request and body, not just the delay.
4. Shared pure `SharedSessionAutoFollow.RunAsync` controls sequential
   read, comparison against selected host and prior epoch/cursor,
   and bounded delay. Failed/forbidden/timed-out host, sequence rewind,
   changed epoch, missing session or unprovable event gap stops the
   observation before showing another trusted event. A new host process
   with the same event sequence still fails closed by W15 epoch.
   At completion it explicitly says no continuous or product ChatGPT
   history was proven.
5. **Stop host follow**, manual refresh, explicit host selection change,
   or window close cancels the work. A generation fence prevents stale
   HTTP results from overwriting a newer selection or closed window.
   The window explicitly reports statuses using accessible polite
   `Sessions.AutoFollowStatus`, and selection/event controls keep
   `ReadOnly`; users must re-read and reselect after a failure.
   Follow is not an always-on service, file-backed scheduler or DLE owner.

## Explicit machine/agent initiation (real localhost qualification)

The existing automation shell now exposes a **separately invoked**
`session-ledger-follow-bounded --port N --session-id GUID-N
--host-instance-id GUID-N --after SEQUENCE --max-observations 2`
command. It calls the **same pure bounded observer** as the human desktop,
with the same 3-second minimum cadence, max-20 cap, fixed localhost GET
reader and epoch/gap refusal. It returns no actual ChatGPT turns and never
starts or mutates a host. Each machine invocation is user/actor-initiated;
there is no ambient daemon or credential signing/minting. An actual W6 host
and independent machine process run two serial observations with a measured
minimum cadence on each of Windows/Linux/macOS in public CI.

## Qualification required

- Deterministic test-double reader: at most one awaited request; 3-read
  bounded happy path; failed host stops without retries; same-sequence
  different-process epoch stops before delivery; truncated event window
  stops; cancellation within delivery prevents another request; status
  never claims complete ChatGPT product history or participant identity.
- Exact compiled Avalonia controls and Windows **live** UI Automation
  peers for `Sessions.AutoFollowStart`, `Sessions.AutoFollowStop`,
  `Sessions.AutoFollowStatus`. A skipped/missing control cannot qualify.
- Original real W6 two-session CLI and agent cursor, W15 process epoch,
  hostile localhost 4s whole-body timeout/64KiB cap, W13 signed bearer
  self-revocation and W9 transactional crash-recovery tests re-run on
  public free Windows/Linux/macOS Github Actions.
- This experiment uses an injected zero-delay **only within pure
  deterministic contract tests**. Production cadence is a fixed 3s;
  clients cannot configure a faster rate via UI.

## Security and product limitations

This does not authenticate OS peers, bind local workbench tab identity to
W6 session IDs, provide human owner-issued grants, independently verify
the host identity, implement a streaming push subscription, perform
continuous backend traffic interception, or mirror native ChatGPT turns.
The per-process GUID is untrusted localhost self-report and may be forged
by a hostile same-user process. Retained event rings can omit history
and optimistic event cursors are not a proof of completeness. W11
disposable WAL store is not production. The W12/W13 signed grant/cached
revoke RDTE does not establish a real user login.

No private source, real authenticated ChatGPT traffic/prompt submissions,
terminal execution, managed host auto-start, local Windows Docker runtime,
physical TrueNAS execution, merge or release without owner gate.

## Next baton

Independently qualify OS-peer and issuer/capability custody; owner
cross-actor revocation; then a single authoritative GUI mutation model
with real HIL. Product integration must preserve ordinary signed-in
ChatGPT entitlements and use only supported/approved integration surfaces.
A browser/agent may not simply claim the W6 metadata log is authoritative
for the ChatGPT product conversation.
