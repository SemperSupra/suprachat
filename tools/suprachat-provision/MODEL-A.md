# Model A: independent private installations

**Owner scope:** Each installed SupraChat is its own MCP provider; one separately owned OpenAI tunnel/managed runtime and ChatGPT registered connection per installation. Multi-node shared MCP gateway (Model B) is deferred future work and is not implemented.

The fleet planner is an **offline set of independent per-install lifecycle plans**, not an active gateway, agent daemon, service broker, or credential distributor. Each resource is identified by kind + workspace scope + key. For connection-bearing tunnel/runtime/ChatGPT registration resources, a shared scope+key or reused remote tunnel ID across different installation IDs is rejected, rather than arbitrarily selecting a provider. It never creates, deletes or connects accounts.

## Cloud acceptance
- Two installations in one workspace with distinct connection keys
- Same logical name across different workspace scopes
- Same-scope connection collision fails
- Duplicate installation identity and shared remote tunnel ID fail
- Safe home uninstall plus independent work install
- Unverified detachment refuses only the affected tunnel
- Stable sorting and per-step scope for automation and agents
- Zero account claims or keys

This layer does **not** prove that the real OpenAI ChatGPT app supports two registrations in a specific account or how an app picks the connection. Those require separate authorized ChatGPT account tests, not fabricated public-runner evidence. The original protected native Windows CI remains a separate gate.
