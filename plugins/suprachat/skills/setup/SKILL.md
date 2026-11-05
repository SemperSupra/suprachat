---
name: suprachat-setup
description: Safely configure and reconcile a reversible SupraChat installation using official OpenAI tools.
---

# SupraChat onboarding

Use Discover → Observe → Plan → Apply → Verify → Reconcile. Begin with the owning DLE and current exact source/CI.

1. Use official OpenAI CLI and Admin API for optional Platform administration; official Secure MCP Tunnel client for scoped tunnel and runtime lifecycle. Keep admin, runtime and API project credentials separate in operating-system secret storage, never Git or public CI.
2. Run the SupraChat provisioning planner to discover owner IDs, scope, resource versions, current fingerprints, drift, prerequisites and protected dependencies. Do not apply until explicitly admitted and verified.
3. A repository-local marketplace entry **does not install a ChatGPT account plugin**. ChatGPT custom MCP plugin registration must follow account/workspace authorization and risk-acknowledgment requirements. Never use browser cookie extraction or undocumented private account APIs.
4. A hosted OpenAI API skill is not a ChatGPT personal plugin skill. Keep API skill versions isolated from local plugin packages and user ChatGPT installations.
5. On upgrade, preserve the owned resource identifier and previous-version rollback record. On uninstall, verify ChatGPT plugin disconnection first, stop local runtime, remove only owned local plugin configuration, and delete only detached, owned Platform tunnels. Project archival requires distinct approval.
6. Preserve third-party, user-created, shared or drifted resources. Never claim full setup merely because a tunnel process is healthy. Verify actual ChatGPT plugin discovery/tool calls separately.
7. Emit redacted receipts, first failures and next action in DLE. Local inventory and credentials remain outside the public repository.
