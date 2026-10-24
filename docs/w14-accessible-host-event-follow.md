# W14 — accessible read-only shared-host session selection and cursor

This is a bounded research integration on W13 [draft PR #52], the single
W6/W9/W11 authority, and the independently qualified W10 desktop observer.
It is not a ChatGPT browser history mirror or production authenticated host.

## Product contract

- The desktop now has an explicitly named and accessible **observed host
  session list**, separate from the existing W0 local session tabs. Selecting
  host A does not call `SessionWorkbench.Select`, change local drafts,
  start terminal processes, navigate browser tabs, submit product messages,
  or grant an actor identity. Each row includes sanitized title, abbreviated
  stable host GUID and revision for ambiguous-title/screen-reader users.
- An explicit **Read shared host inventory** refresh uses the same fixed
  127.0.0.1 GET and bounded read deadline as earlier W10. Nothing polls in
  background or starts a host. The selected host ID and last observed global
  event sequence are local *observation cursors*, not a second mutation ledger.
- W6 inventory event validation now checks bounded ascending sequence,
  canonical/session-present ID, signed-audience **label** whitelist and
  known metadata operation types. It exposes only immutable event
  metadata (session ID, audience label, operation, revision, sequence), no
  prompt text, response contents, credentials, cookie headers or URLs.
- One pure `SharedSessionFeedProjection` is used from both Avalonia and
  machine CLI `session-ledger-follow --port ... --session-id ... --after ...`,
  plus JSON-RPC `session/ledger/follow`. It returns an explicit status
  and first-newer events for the selected session (max 12 shown). A CLI/agent
  caller maintains its own after-sequence; the GUI maintains a distinct
  cursor for each observed host port. No client may mutate the W6 ledger
  merely by reading the cursor.
- If a host's sequence rewinds, a previously selected ID disappears or an
  event window is lost/truncated, the projection explicitly reports
  `SEQUENCE_REWIND_RESELECT_REQUIRED`,
  `SELECTED_SESSION_MISSING_RESELECT_REQUIRED` or
  `EVENT_GAP_UNVERIFIED`. A port switch invalidates selection. Absence of
  a detected gap is *not proof of complete history*, an exact host instance,
  or remote ChatGPT product conversation continuity.
- Existing W13 signed-grant/revocation and W11 W6 durable history remain
  authoritative and untouched. This is a **read-only observation feature**,
  not migration of W0 sessions into live W6 sessions.

## Empirical and negative qualification

- Pure independent contract tests: selected session A vs B, advancing
  cursor, repeat no duplicates, retained-window bootstrap, missing host
  selection, sequence rewind, truncated history and no workbench or
  browser/terminal authority.
- Real W6 host on disposable public GHA: two sessions opened in separate
  human/agent label lanes; process-independent CLI/JSON-RPC readers select
  different host IDs; advancing A title creates a single new event for A
  while B remains isolated; stale cursor and host restart require explicit
  re-selection. Host origin/bearer rules remain. No real ChatGPT prompts.
- Existing hostile localhost body size, deadline, malformed source and
  privilege-injection negatives re-run on three platforms.
- Dedicated and full application CI require Windows live native UI Automation
  peers for `Sessions.SharedSelectionHeading`,
  `Sessions.SharedHostSelection`, `Sessions.SharedHostEvents` and
  `Sessions.SharedHostFollowStatus`. A skipped/missing peer is not a UIA PASS.
  Existing intermittent `Auth.SignIn` attach flake remains a separate
  known build gate, not an excuse to disable assertions.

## Remaining explicit limitations

This is **not** live incremental push subscription. It is explicit manual
refresh with cursor-based bounded catch-up. A host can restart and reuse the
same port and sequence without unique instance attestation. The W6 event
ring may discard old entries; gaps are detected when sequence evidence
allows, not prevented. The common read bearer still does not verify
individual OS peers; W13 self-revocation is not owner revocation; signer
key custody is synthetic. W9/W11 storage is not production-admitted.

The real signed-in ChatGPT product conversation remains authoritative for
account history/entitlements. This host does not synchronize all product
turns or programmatically extract outputs. Browser actuation, any
provider POST, physical devices, private source, owner merge/release, and
Windows Docker installation are outside this work.
