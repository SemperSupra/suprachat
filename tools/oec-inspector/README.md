# OEC Inspector P0 — public-synthetic-only

Official `@modelcontextprotocol/inspector@2.13.0` CLI probes seven-tool OEC Observer MCP v0.2 (stacked on draft PR #144) over local authenticated HTTP. Runs **real Inspector CLI** initialization, discovery, capability call, and invalid-bound negative case. Captures only a fixed allowlist and boolean outcomes in the evidence artifact; raw inspector output, bearer, OS keyring files, and user data are never uploaded.

Pin Node 24.22.1 or later for patched runtime certificates. Inspector can write secrets to disk if a keyring is unavailable; run with a temporary HOME, delete it on exit, and never inspect production secrets in public Actions. This is **MCP protocol interface qualification**, not an assertion of hidden ChatGPT internals or complete upstream server compliance.

No product Core/UI/Auth or protected runners touched. Git/DLE owns source and compact receipts. Release and merge held.
