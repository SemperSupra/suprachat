# Disposable public GHA × Cloudflare Quick Tunnel

Scope: cross-project, **synthetic-only** free public runner experiment. This is not a ChatGPT internal introspection tool, credential broker, Work bridge or production plugin.

## Acceptance

1. Start a Go Streamable HTTP (POST JSON-only) MCP server on `127.0.0.1:18761` inside a disposable public Linux runner.
2. Generate a fresh runner-local bearer token, download a fixed-checksum cloudflared binary, and establish a temporary HTTPS `trycloudflare.com` Quick Tunnel.
3. Make real client HTTPS requests through Cloudflare: `initialize`, `tools/list`, `lab.observe`, `lab.digest`. Verify remote and loopback nonce equivalence; verify SHA-256 challenge digest; enforce wrong-token 401, foreign-Origin 403, and unauthorized-tool refusal.
4. Upload only a sanitized evidence receipt and cleanly kill both processes even on failure.

## Protocol and security limits

Cloudflare Quick Tunnels currently **do not support Server-Sent Events**. This deliberately uses the JSON response branch of MCP Streamable HTTP POST with protocol version `2025-06-18`. No SSE, live ChatGPT access or comprehensive MCP compliance claim.

The random HTTPS hostname is public ingress, never an authorization secret. Bearer token is generated per job and never uploaded, echoed or used in an untrusted PR input. The listener binds loopback only and refuses browser Origin headers. The token and temporary URL remain runner-local; the Cloudflare log is never published.

The runner makes its external request to Cloudflare and observes the same live server nonce as its local request. This proves a public HTTPS ingress roundtrip but **does not prove an independent second runner**, and does not reveal ChatGPT's hidden runtime. For cross-runner testing, add an authenticated rendezvous with a distinct trust boundary.

Public PR workflow uses only public source, ephemeral random token, deterministic fixture methods and publicly available binary. No third-party account, provider APIs, private repo, work credentials, personal data, private GitHub Actions minutes, merge, release or production deployment.

Quick Tunnel hostnames rotate, no uptime guarantee, a 200 in-flight request limit, and interactive email authentication is unsuitable for noninteractive clients. For persistent production use, choose an approved stable authenticated tunnel or separate service gateway.

## Reuse in other public-runner projects

These components are self-contained: `mcp.go`, `cmd/lab-mcp`, `run-quick-tunnel.sh`, `verify_remote.py` and `.github/workflows/qualify-ephemeral-quick-tunnel.yml`. Qualify them here, then copy or make a pinned reusable action across other public, source-safe repositories. Keep a separate token, program allowlist and bounded lifetime for each job.

Locally: `go test ./...`; `go run ./cmd/lab-mcp` requires a 48+ character `OEC_LAB_TOKEN`. The network experiment is the Ubuntu public GHA `quick-tunnel-e2e` job.

## Custody and next gates

Public Git/CI hold only synthetic source and redacted receipts. Private DLE #2 owns decisions, evidence links and next baton. Other programs must independently admit a runner, fixed capabilities, source sanitation and credential boundaries. No general-purpose shell, private host, authenticated browser, dynamic plugin loader or production session exposed via Quick Tunnel.
