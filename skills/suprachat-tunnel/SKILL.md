---
name: suprachat-tunnel
description: Safely reconcile, qualify, and bootstrap the official OpenAI Secure MCP Tunnel for SupraChat without credential disclosure
---

# SupraChat tunnel skill

Follow Discover → Observe → Plan → Apply → Verify → Reconcile. Current authority is private SemperSupra/suprachat-private issue #2 and public source exact heads, not chat history. Do not bypass refused bridge/merge writes, actor path leases, or release gates.

## Workflow
1. Query upstream tunnel-client docs and release checksums; verify the binary's exact version and source before use. Prefer native `runtimes connect/status`, `doctor --json`, and Codex plugin commands over bespoke tunnel protocols.
2. Read account and connector entitlement/roles from the authorized operator, not inferred from ChatGPT plan or synthetic tests. Differentiate Platform admin key, restricted runtime key, tunnel id and ChatGPT connector permission. Never ask users to paste keys into chat.
3. Run only dry preflight in public CI. Use `go test ./...` under `tools/oec-tunnel-bootstrap` plus the pinned-official-binary help workflow. Test malicious endpoint, scope swapping, missing credentials, stale alias, unhealthy/false-ready, wrong tunnel id, optional Codex plugin failure and stop/refusal.
4. For actual provisioning, obtain explicit scoped operator authorization. Use `tunnelctl --action connect --execute` against a verified official local executable and a narrowly allowlisted localhost MCP service. Do not use an untrusted generic browser CDP endpoint, raw shell, cookies or page text.
5. Verify `runtimes --json status` independently for running, healthy and ready. Treat signed-in ChatGPT access, ChatGPT connector discovery, MCP tool calls, and WebUI observation as distinct separate checks. Do not claim a connector was installed by a successful tunnel process.
6. ChatGPT custom app installation may require account/workspace developer permissions and UI confirmation. Report blocked prerequisites clearly; never pretend to click or configure a remote account without a supported authorized action.
7. Reconcile PR, branch, CI, DLE, source leases, evidence and first failures after every batch. Close only proved superseded work. Preserve all secrets locally. Public Git gets tests and synthetic receipts; restricted Drive may hold large approved nonsecret deliverables with hashes.

## Audience contract
- Human: simple preflight states and clear exceptions.
- Automation: strict JSON receipts, versioned schemas, exit codes and finite timeouts.
- Agent: structured capabilities, ownership, CAS-safe actions, no inherited credentials, durable evidence link.

## Stop conditions
No authenticated connector permissions, wrong tunnel id/scope, unsupported app plan, missing secret keys, native UIA gate failure, unverified source, unexpected remote endpoint, broadened MCP tool scope, or unexplained network response: stop only the affected lane, maintain independent cloud work.
