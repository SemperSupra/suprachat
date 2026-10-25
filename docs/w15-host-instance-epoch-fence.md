# W15 — host-process epoch fence for W14 metadata observers

**Research-only, bounded same-user loopback continuity discriminator.**
This is NOT authenticated host identity, a trusted ledger epoch, device
attestation, an authenticated user, or remote ChatGPT conversation history.

## Problem and bounded mechanism

W14 tracks selected host session ID and global event sequence but the host
could be killed and replaced, reusing the same port with the *same* sequence,
or a W11 durable host could restart with retained IDs and version. Sequence
equality would otherwise produce a false `SELECTED_NO_NEW_EVENTS` receipt.

W15 extends the W6 host's existing `SessionLedgerInventory` response with
an optional `HostInstanceId` (canonical lowercase GUID-N). A new random
instance ID is allocated at **every actual host process startup** and
emitted consistently in its health, inventory and ready receipts. This ID is
NOT stored in W11's durable checkpoint: restoring durable state is precisely
why a new process epoch must be observed. It is not a signing key.

The W10/W14 read-only `SharedSessionInventoryReader` validates the epoch's
strict canonical format, retains existing response size/timeout/network/
privilege denials and still accepts older W6 inventories without an epoch
as **unverified legacy observations**. Its typed receipt exposes the value
only as an unauthenticated self-report.

`SharedSessionFeedProjection.Project(observation, selectedId, lastSequence,
previousHostInstanceId)` compares a caller-held previous epoch **before**
sequence, event window and selected session. A replacement or missing epoch
when a prior epoch was supplied produces
`HOST_INSTANCE_CHANGED_RESELECT_REQUIRED` with **no selected session,
no events, and explicit uncertain continuity**. Invalid caller epochs produce
`INVALID_HOST_EPOCH`. The prior cursor remains caller-owned and no server
mutation, synthetic replay or implicit local-tab/browser binding occurs.

Avalonia's host observer holds previous instance ID separately from local
W0 tabs and reset it only on explicitly changed observed port. After epoch
change it drops selection; a human must explicitly reselect the observed
host session. CLI `session-ledger-follow` accepts
`--host-instance-id <previous-guid-N>`; JSON-RPC
`session/ledger/follow` accepts `hostInstanceId` alongside
`afterSequence`. Both use the same shared core as the desktop.

## Qualification

- Pure .NET tests: same host + same sequence means no new events; another
  host with the **same sequence and same selected ID** forces reselection;
  missing legacy epoch or malformed client epoch cannot inherit continuity.
- Actual W6 host: health, CLI inventory and agent metadata all report same
  instance on one host; restart creates different instance; independent
  human-style CLI and agent readers reject a previous epoch even when the
  new host has been advanced to the **same global sequence**.
- Hostile localhost fixture: malformed or non-canonical host instance
  refused; legacy no-epoch response remains readable but cannot establish
  continuity once a previous epoch is supplied. Re-run existing W14
  two-session observer, W13 signed-write/revocation, W9 crash recovery and
  Windows native semantic accessibility (no skip/fallback claim).

## Unresolved / separate gates

A malicious same-user process may impersonate a host and forge any claimed
epoch: GUID randomness alone is not authentication. Same-port replacement
can still be spoofed by a forged HTTP server; independent OS peer and owner
binding required before actual multi-user trust. No long-lived background
poller/stream is introduced here, no proof of continuous event history,
and no remote ChatGPT product turn synchronization. Existing W11 disposable
storage and W13 self-revocation are NOT production-admitted; no owner
cross-actor revocation, token key custody or identity attestation.

No owner merge/release, real provider POST, terminal action, browser cookie
capture, private source, Windows Docker installation or TrueNAS usage.
