# Official OpenAI Secure MCP Tunnel — SupraChat automation

This tool composes upstream `openai/tunnel-client` v0.0.19 managed-runtime commands rather than implementing a second tunnel protocol. Public Actions test **only mock command outcomes and upstream help**, with no API keys, ChatGPT login, private hosts or actual control-plane tunnel provisioning.

## What can be automated
- Validate fixed loopback MCP URL, known tunnel ID, alias, explicit Platform org/workspace scope.
- Emit JSON preflight without modifying a user system.
- Reuse an existing tunnel through `runtimes --json connect --tunnel-id ...`, or, **only with `--allow-create`**, use the upstream managed create/reuse flow with org/workspace attachment and an admin key.
- Launch a managed native profile (official upstream runtime supervision, not nohup) and **independently** check `runtimes --json status <alias>` for `process_running`, `healthy` and `ready` all true.
- Optionally install/check **Codex's** official tunnel plugin with `--install-codex-plugin`. It does **not** install or create a ChatGPT custom app.
- Stop only the named managed runtime when `--action stop --execute` is explicitly given. It does not delete the remote tunnel or ChatGPT connector.

## Security and operation
Runs are dry-run by default, mutations require `--execute`, creation requires `--allow-create` plus `OPENAI_ADMIN_KEY` in the local environment and at least one explicit org/workspace ID. Reusing an existing tunnel ID needs only a restricted `CONTROL_PLANE_API_KEY` with Tunnels Read+Use. Admin credentials are not inherited by runtime status or Codex plugin commands. Never pass keys on argv, write them to GitHub Secrets or upload them as artifacts. The CLI deliberately reports generic errors rather than echoing upstream output. The local MCP service should expose only allowlisted structural/test tools, not arbitrary CDP, a shell, browser cookies or prompts.

Before any local use, obtain the official binary and verify its release signature/checksum using the existing `tools/oec-tunnel-client-offline` qualification. The caller must supply its absolute path and own the local fixture/server. Example dry plan:

```powershell
$env:CONTROL_PLANE_TUNNEL_ID='tunnel_0123456789abcdef0123456789abcdef' # use actual value locally
go run ./cmd/tunnelctl --action plan --alias suprachat-rdte --mcp-url http://127.0.0.1:18761/mcp
```

After an operator provides restricted credentials locally (not in chat or source), the same command with `--action connect --execute --binary 'C:\PRIVATE\tunnel-client.exe'` will invoke the official managed runtime path. A `tunnel_id` is **not** a runtime key, and a ChatGPT custom connector is **not automatically created** by this CLI.

Account/app setup remains a user-side action, if the account is eligible. See [Platform Tunnels](https://platform.openai.com/settings/organization/tunnels), [Runtime API keys](https://platform.openai.com/settings/organization/api-keys), [Admin keys](https://platform.openai.com/settings/organization/admin-keys), and [ChatGPT Apps/Connectors](https://chatgpt.com/#settings/Connectors). ChatGPT workspace capability/plan and connector permission must be checked independently. Ordinary logged-in ChatGPT WebUI access is separate from creating an MCP connection for ChatGPT to use your tools.

## Qualified tiers
T0 mock lifecycle negatives + Go vet/test/race on Windows/Linux/macOS; upstream official CLI help checked with pinned SHA-256 on public Linux runner. T1 disposable synthetic real Cloudflare HTTPS proof in #162. T2 actual OpenAI control-plane connection, health, connector registration and cross-session tool call requires local authorized account/key and permission, not public GHA. T3 owner authenticated WebUI observation and real action is **not** established by T0/T1/T2. Runtime ready does not prove ChatGPT connector installed.

Source and tests: public Git. DLE and exact CI receipts: private #2. No public cloud artifacts may contain credentials, private browser state or ChatGPT conversation content. Do not promote a dry-run or fake upstream status to live acceptance.

Official prior art: https://github.com/openai/tunnel-client/blob/v0.0.19/docs/onboarding.md and https://github.com/openai/tunnel-client/blob/v0.0.19/docs/end-user-guide.md.
