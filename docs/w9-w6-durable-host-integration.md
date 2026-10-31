# W9 W6 host durability integration — synthetic, draft, never production data

This W9 COW branch is stacked on the green W7 .NET SQLite experiment PR #42,
which in turn is stacked on qualified W6 shared loopback host PR #37. The W9
work integrates SQLite persistence into the **same** SharedSessionLedger used
by W6 HTTP, CLI and agent JSON-RPC clients, not an independent second registry.

## Scope and invariants

- **Default W6 stays ephemeral** when `SUPRACHAT_LEDGER_STORE` is unset.
  Historical W6 ephemeral tests continue to assert a restart clears state.
- When the operator deliberately sets `SUPRACHAT_LEDGER_STORE` to an absolute
  file path under an existing disposable directory, the W6 host obtains a
  lifetime exclusive file lock, validates SQLite integrity/schema, and restores
  the full bounded ledger checkpoint (all sessions, events and immutable
  first-result idempotency entries) **before opening its HTTP listener**.
- W6's original shared registry is the **only mutation authority**. It
  serializes mutations under its own lock, captures a checkpoint and calls the
  SQLite WAL FULL writer before returning **either** APPLIED or an immutable
  rejection. A failed SQLite commit kills the host; stale in-memory revisions
  can never become observable as successful committed mutations.
- Repeated same key + same payload gets the historical first receipt, including
  old VERSION_CONFLICT; changed payload is refused. Event sequence and all
  sessions are recovered together. The checkpoint uses SHA-256 content integrity
  and SQLite `PRAGMA integrity_check`; incompatible/corrupt stores fail closed.
- Existing bearer + Origin/localhost request admission is unchanged. There is
  NO separate actor identity, terminal action, ChatGPT prompt send, provider
  access, or automatic local host launch.
- `/v1/health`, `/v1/sessions` and ready metadata truthfully advertise
  durability only when an explicit W9 store was opened successfully.
  `authenticated_audience_identity=false` is preserved in ALL modes.

## Actual causal W9 test

A public disposable GHA host starts W6 as a real .NET process, with an explicit
SQLite file. Existing W6 HTTP and agent JSON-RPC machine clients open two
sessions and race writes to the same A revision, while B progresses. The host
is killed and restarted; both session IDs and first accepted/rejected receipts
must survive. Next, **fault-injection-only hosts** with
`SUPRACHAT_W9_RDTE_FAULTS=1` deliberately exit at the pre-commit and
post-commit/pre-HTTP-ACK boundaries. The test checks rollback or immutable
replay without duplicate mutation. A second concurrent store owner and
corrupt database startup must be denied. Existing W6 ephemeral tests run in
the same CI matrix on Windows, Linux and macOS.

The test-only fault variables are not a public API or operator override for
external actions and do not contact a real provider. Do not set them in real
workloads.

## Remaining restrictions / independent acceptance

1. This is still **public synthetic metadata only**; content is stored
   unencrypted in a disposable local SQLite file and WAL. Storage path
   parent ACL/custody, symlink/hardlink threats, backup/migration, actual
   hardware power-loss, disk-full, partial fsync, and encrypted private
   metadata are unqualified. Never use real session URLs, conversation text,
   access tokens or personal information as fixtures.
2. A same-user malicious process with the W6 bearer can impersonate any
   audience. W8 scoped bearer RDTE PR #44 is independently qualified but
   NOT merged into W9. Bearer grants alone do not verify a human's identity;
   real OS-peer binding, trusted issuer, key custody and durable revocation
   must precede the claim `authenticated_audience_identity=true`.
3. W6's caller-supplied `audience` remains an **untrusted label**. The current
   W9 durable mode is *not production-admitted*, and no migration from legacy
   W1/W2 local workbench or W6 live ephemeral session is performed. W6
   individual browser conversation binding and true product conversation
   send/read are not provided.
4. Full keyboard/screen-reader and UIA causal qualification must integrate
   the independently green read-only desktop PR #43 with the actual durable
   host; W9 alone does not bind accessible GUI sessions for mutation.
5. No automatic merge/release, no private source export, no Docker/Engine
   or alternative daemon installation on Windows dev machine, no physical
   TrueNAS capacity allocation, no live provider prompts.

## Reproduce

```sh
dotnet build SupraChat.Automation/SupraChat.Automation.csproj -c Release
node tools/qualify-w9-durable-w6-host.mjs
node tools/qualify-w6-shared-ledger.mjs
```

The reproduction harness creates its own temporary store/token/port and
cleans up. It must never use an existing user file or real account data.
