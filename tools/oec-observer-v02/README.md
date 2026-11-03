# OEC Mirror Observer MCP v0.2

A strictly bounded, public-safe, seven-tool **synthetic** MCP observation service. It supports capability discovery, runner environment metadata, simulated UI snapshots/diffs, explicitly admitted counter experiment, verification and hashed receipts.

Tools: `mirror.capabilities`, `mirror.environment`, `mirror.ui_snapshot`, `mirror.ui_diff`, `mirror.experiment`, `mirror.verify`, `mirror.evidence`.

The initial UI is an in-memory accessibility-like counter fixture, **not an actual browser, ChatGPT session, WebView2 runtime, or inference implementation**. Evidence labels deliberately distinguish `DIRECT_RUNNER` from `SYNTHETIC_MODEL_ONLY`. Never infer hidden model behavior.

Only authorized POST JSON is accepted at `/mcp`. A fresh bearer token is required; Origin-bearing requests are refused, request bodies are bounded, and there is no shell execution or filesystem access. Synthetic experiment mutations are limited to eight increments and idempotent by request ID; `admit_synthetic:true` must be set. The model never uploads prompts, responses, credentials, cookies or host paths.

Test with `go test ./... -count=5`. Start locally with a fresh long `OEC_OBSERVER_TOKEN` and `go run ./cmd/observer` from this module. The independent public GHA qualification verifies Linux/Windows/macOS source and synthetic contracts.

**Next step:** connect an independently admitted Playwright/Chrome DevTools fixture observer behind the same names and evidence schema; qualify a separate runner as the witness; add human/automation/agent audience parity and EN/DE/TH. Do not move source into production Core, claim privileged OpenAI introspection, attach authenticated ChatGPT or merge/release without owner gate.
