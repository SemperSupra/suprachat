# W14: Official ChatGPT MCP conversation correlation — bounded acceptance contract

Status: **synthetic, research-only**. Owner workstream: [SupraChat #19](https://github.com/SemperSupra/suprachat/issues/19).
Traffic observation is a separate [W12 #51](https://github.com/SemperSupra/suprachat/issues/51) effort.

## Problem

SupraChat's main product conversation must remain an authentic, signed-in `chatgpt.com`
conversation, using the normal ChatGPT chat entitlement. SIWC direct Responses and Codex
are **different provider/entitlement planes**, and neither is a way to edit an existing
ChatGPT-owned conversation. The independent, authoritative DLE workstream must be available
to qualified humans (including assistive tech), automation and agents.

The official OpenAI MCP Plugin API exposes an anonymized conversation-correlation hint:
`_meta["openai/session"]` on tool calls. This gives us a **supported correlation primitive**,
not a stable public ChatGPT conversation URL, transcript or turn ID, a write-through sync API,
or authorization to use private ChatGPT browser endpoints.

References, checked 2026-10-24:
- https://developers.openai.com/plugins/reference
- https://developers.openai.com/plugins/changelog
- https://developers.openai.com/plugins/build/auth
- https://developers.openai.com/api/docs/guides/custom-mcp-server
- https://developers.openai.com/plugins/build/extensions
- https://openai.com/policies/eu-terms-of-use/

## Distinct objects and non-authorities

| Object | Authority | Safe projected operations |
| --- | --- | --- |
| ChatGPT product conversation | Official ChatGPT product | Human browser navigation and product-native conversation turns |
| MCP `openai/session` metadata | ChatGPT-hosted tool invocation, if transport verified | Correlate multiple invocations; no transcript read/write inference |
| Authenticated user/principal | **Our** MCP OAuth/resource server and actor-identity policy | Authorize against DLE workstream ACL and per-operation grants |
| DLE workstream/session binding | Existing project-authoritative DLE/W6 host | Bind/inspect with explicit consent and CAS/idempotency |
| SIWC/Codex Responses thread | Independent provider | Only separately authorized, entitlement-labelled model inference |
| Browser resource trace | #51 independent observer | Consent-gated, redacted local metadata; never as identity or history authority |

Do not use `openai/session`, `openai/subject`, locale, browser URL, model text,
or a local bearer shared by multiple actors as the sole authentication or authorization
basis. In production verify bearer token signature, issuer, resource/audience, time
claims and scopes; use TLS client identity where configured. Session hints can be absent,
rotated, or interpreted differently by future host versions. No fallback to another
binding or account, and no claim of universal conversation-history enumeration.

## Planned host integration (NOT implemented by this change)

1. User invokes a designated SupraChat MCP tool from the relevant **real** ChatGPT conversation.
2. Trusted MCP middleware establishes the authenticated application principal and validates
   scope; only then it passes trusted request metadata to the adapter.
3. The adapter checks for `openai/session`, creates a pending **non-authoritative**
   correlation and presents a clear workstream selection/confirmation to the person.
4. The authorized actor confirms binding to an existing DLE workstream. W8 principal,
   W6 CAS, duplicate-effect handling, and durable self-revocation remain mandatory.
   The production binding is stored **by the existing single authoritative host**,
   not by the fixture oracle, plugin UI, trace store or a second database.
5. All human/assistive, CLI and agent surfaces receive a sanitized `BOUND|UNBOUND|REFUSED`
   projection from the core, subject to their own permissions. The raw correlation hint
   is never logged, returned in a receipt, passed through public CI, or copied into GitHub.
6. Optional plugin UI `window.openai.sendFollowUpMessage` can be qualified as an
   **interactive, scoped, ChatGPT-owned UI action**. It is not a general headless send
   API for agents. Treat submission outcome as UNKNOWN unless the host supplies proof.
7. Unsupported external transcript/turn read/write remains typed
   `BLOCKED_SUPPORTED_INTERFACE`. Use officially supported manual exports only as
   bounded historical snapshots.

## Synthetic qualification provided here

`tools/test_plugin_session_binding.py` is an executable stdlib-only reference model
and negative-test oracle. It has **no** user session, OAuth keys, network capability,
actual MCP server, persisted database, or browser control. It deliberately models
trusted identity and transport as test inputs so it cannot claim to verify OAuth itself.

Execute:

```bash
python3 -m unittest discover -s tools -p 'test_plugin_session_binding.py' -v
```

Twelve tests cover:
- explicit user confirmation and ACL before binding;
- two independent conversations, same authenticated principal;
- two authenticated principals with the *same* anonymized hint, isolated;
- idempotent repeat binding without mutation, conflicting rebind refused;
- no auth / untrusted transport / missing, malformed, oversized session hint;
- forged `openai/subject` cannot impersonate an authorized principal;
- incorrect workstream context refused;
- absent transcript-API capability is explicit and fails closed;
- redaction of opaque session/subject, token and prompt sentinel values;
- unknown operation refused.

**This is not a W6 ledger implementation**: immutable first-outcome receipts,
CAS, persisted actor revocation, and actual principal identity are tested by their own
branches and must be integrated after independent qualification. This reference deliberately
cannot issue tokens, send to ChatGPT, or write to DLE.

## External HIL acceptance gates (not passed)

1. A user installs an approved test-only SupraChat MCP plugin, using official
   remote MCP registration. Confirm user authentication, exact connected account,
   scope, audience and per-tool write approvals.
2. Two genuine ChatGPT conversations invoke a harmless tool and prove distinct
   correlation values for the same verified principal without printing the actual values.
   Revisit one conversation; prove stable correlation over the tested interval,
   then clearly label any reconnection/rotation behavior **observed**, not guaranteed.
3. Two distinct authenticated principals cannot observe or actuate each other's
   workstream, even when an untrusted metadata hint is the same.
4. The same workstream is visible to human/accessible UI, CLI and JSON-RPC
   through **one** DLE authority with verified revision/permission semantics.
5. Explicit user-initiated UI follow-up, if enabled, is tested separately
   and never masquerades as a remote CLI send. No automatic transcript extraction,
   cookie copy, undocumented backend call, or browser message replay.
6. Independent privacy/security review, exact-source cross-platform qualification,
   and explicit owner merge/release decision before production admission.

## Decision and stop conditions

This supported route **can** synchronize selected *workstream facts/actions* across a
real ChatGPT conversation and external clients. It **cannot** promise complete real-time
ChatGPT message-history synchronization or multi-writer concurrency against mobile/browser
turns. If the owner's requirement demands arbitrary turn-level read/write, keep it
`BLOCKED_SUPPORTED_INTERFACE` until OpenAI provides an authorized integration contract.
Do not rebrand a fragile unofficial bridge as supported sync.

Use prior art (DrA1ex/chatgpt-bridge et al.) as an independent **synthetic
algorithm/test oracle** for leasing, ambiguous effects, idempotency and recovery only.
Do not adopt unofficial ChatGPT traffic extraction in this work package.
