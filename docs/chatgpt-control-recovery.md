# SupraChat #19 — ChatGPT-specific control recovery / W0 vertical slice

As of 2026-10-20. This is an **implementation record**, not a scope change or new project.

## Product objective

From human, keyboard/screen-reader, deterministic automation and agent surfaces,
control **the same identified existing ChatGPT product conversation**:
observe readiness, explicitly submit a selected prompt, observe the matching
completed assistant turn, prepare a handoff, explicitly transfer content to/from
a paired terminal, and export permitted session material.

The existing **SIWC/Responses/Codex** lane remains separate: a model/API
response is **not** a reply in a pre-existing ChatGPT WebUI conversation.

## Ownership and reuse

- [SupraChat #19](https://github.com/SemperSupra/suprachat/issues/19):
  product contract, workbench, accessible GUI, CLI and JSON-RPC, exports.
- [SupraChat #14](https://github.com/SemperSupra/suprachat/issues/14):
  optional remote/PWA consumer, gated by credential-broker acceptance.
- [Userscripts #49 / PR #50](https://github.com/SupraShellScripts/userscripts-private/pull/50):
  existing loopback-only ChatGPT prompt/readiness/result bridge candidate.
- [Userscripts #87 / PR #88](https://github.com/SupraShellScripts/userscripts-private/pull/88):
  current non-content handoff helper.
- [Userscripts #92 / PR #93](https://github.com/SupraShellScripts/userscripts-private/pull/93):
  opt-in bounded continuation prototype. **Not an authority source.**
- [Violentmonkey Workbench #139](https://github.com/SupraShellScripts/violentmonkey-workbench-private/issues/139):
  canary -> handoff -> autonomy -> bridge safety and lifecycle gates.
- [BrowserParity #76](https://github.com/SemperSupra/BrowserParity-private/issues/76):
  observation/intervention qualification and target knowledge; don't duplicate.
- [SurgeHalo #13](https://github.com/SemperSupra/surge-halo-private/issues/13):
  multi-agent/browser/terminal session *lifecycle*, not SupraChat product control.
- [Agent Dispatch private #110](https://github.com/SemperSupra/agent-dispatch-private/issues/110):
  authoritative unattended workstream control; do not treat ChatGPT transcript as DLE.

## Batch R0: delivered in this draft PR

The native .NET Core now advertises a **closed, versioned product-control
contract** with a read-only, conservative conversation URL inspector.
Its human-observable browser adapter remains \`NOT_CONNECTED\`. No request
is issued to ChatGPT, and no authentication/private browser state is used.

Both deterministic automation (\`chatgpt-control\`,
\`chatgpt-session-inspect --url ...\`) and agent JSON-RPC
(\`chatgpt/control/read\`, \`chatgpt/conversation/inspect\`) call the
**same** core methods. Contract tests cover a recognized conversation,
a GPT-prefixed conversation, and rejected origins/metadata/ambiguous routes.
Existing public GitHub Actions qualifies tests on Windows/Linux/macOS.

This establishes *method identity and safe status*, **not** permission to send
prompts, access cookies, or claim live ChatGPT control.

## Parallel implementation lanes (existing authorities)

### A — ChatGPT bridge consumer (critical path)

Reconcile private PR #50 against current ChatGPT UI and current Workbench
policy; independently run existing deterministic tests on an admitted
private executor. Close recorded defects. Only after an authorized
human-owned authenticated browser rep should a thin adapter consume
readiness, submit, correlated turn completion, and cancellation.

Required proof: given one exact recognized conversation ID, one controlled
prompt reaches exactly that conversation, produces exactly one attributable
assistant turn, and returns terminal success/error/uncertain states. Reject
other tabs, stale conversation IDs, duplicate requests, human drafts and
uncertain completion. Never infer capability from synthetic fixture PASS.

### B — Handoff and bounded continuation

Use existing PR #88 and PR #93 rather than re-implementing in SupraChat.
Keep their different authority envelopes visible: a handoff helper does
not auto-submit; an autonomy lease has narrow opt-in/stop limits, and
neither is an unattended infrastructure controller.

### C — Workbench session/terminal/export

From #19, implement the smallest one-conversation/one-terminal surface
and *preview-only* directional transfers. The terminal executes only after
a separate explicit action and existing permission check. Export must
distinguish user-supplied/transiently observed visible content from
provider-owned conversation history; fail closed for unavailable branches
and restricted material. Versioned JSON is portable source; Markdown/PDF
are optional representations. Do not build another session supervisor.

### D — Audience parity and independent qualification

One common operation ID and status/error taxonomy across accessible GUI,
CLI, and JSON-RPC. Negative UIA/keyboard/secret-spill/replay tests are
mandatory. Hosted CI may only use public synthetic fixtures. Live account
and terminal actions require separate local HIL with privacy-safe receipts.

## Dependency and batch order

- **Parallel now:** A static/source red-team + private admission review;
  B current-PR exact-head review; C core/workbench code using synthetic
  fixtures; D deterministic negative tests/public GHA.
- **Sequential:** accepted browser adapter -> authorized live ChatGPT
  HIL -> real conversation correlation -> safe terminal handoff and
  bounded export -> full accessible end-to-end parity -> promotion.
- **Independent:** SupraSession/Agent Dispatch autonomy gate remains
  separately owned by #110 and should not block local workbench UX.

## Acceptance oracle for the first real integrated rep

1. Human selects a logged-in ChatGPT conversation; a reader returns the
   same conversation identity across human/accessibility/CLI/agent views.
2. Agent or CLI submits one approved prompt through a bound, qualified
   provider adapter; no other conversation changes.
3. Readiness/stream/turn-completed/cancel/error states are correlated with
   that request; no result is inferred from timeouts or page chrome alone.
4. Operator chooses a selected reply and a paired terminal. The initial
   operation **previews/inserts** only; no command is executed.
5. A source-bound export includes only observed/authorized data, provenance,
   schema version, redaction and incompleteness classifications.
6. Authentication, rate limit, navigation drift, re-auth, duplicate submit,
   identity mismatch, sensitive output and ambiguous UI all stop or fail
   closed; no hidden backend endpoints, cookie scraping, or credential relay.
7. Windows causal HIL and independent public-safe CI evidence are recorded
   with exact build HEAD. Owner review/merge is separate from CI success.

No private repository source, credentials, authenticated conversation bodies,
or WebView storage may be committed or uploaded to public CI.
