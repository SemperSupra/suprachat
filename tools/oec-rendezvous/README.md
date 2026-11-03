# Disposable two-runner Cloudflare MCP rendezvous

This independently qualifies the existing source-safe `tools/ephemeral-tunnel-lab` fixed MCP fixture from unmerged parent draft PR #143 on **two concurrent, separate public GitHub Actions jobs**. It does not introspect ChatGPT, operate an authenticated browser, grant general remote execution, access private source, or merge/release.

## Rendezvous protocol

1. **Client runner B** generates an ephemeral RSA-3072 key pair. The private key never leaves the runner; only `client.pem` is uploaded as a public-key artifact for this workflow run.
2. **Server runner A** downloads that run-scoped public key via the GitHub Actions API with read-only permissions, starts an authenticated loopback MCP fixture, checks the pinned official cloudflared SHA-256, launches a temporary Cloudflare HTTPS tunnel, and RSA-OAEP-SHA256 encrypts its one-time bearer token for B.
3. A publishes only `endpoint.json` (a public temporary URL, **not a secret**) and `encrypted-token.bin`. B downloads both from the same workflow run, decrypts the token exclusively inside B's ephemeral filesystem, and calls the actual Cloudflare HTTPS endpoint.
4. B verifies MCP initialization, two allowlisted tools, live-process nonce, random challenge digest and unauthorized 401, untrusted Origin 403, unknown-tool rejection. B uploads a sanitized receipt.
5. A checks B's receipt, then kills the public tunnel and local MCP server and destroys its local token. B destroys its private key and token.

This design uses `GH_TOKEN` only for **actions:read** on the same workflow run. It never stores bearer tokens, private keys, messages, prompts, authenticated sessions or local system identifiers in public artifacts or logs. Artifacts retain only public keys, temporary URL, encrypted token and redacted report (one day).

**Qualification limits:** This is a proof of two real independent GHA jobs, but it relies on GitHub artifacts as a run-scoped rendezvous channel. It is not a production identity platform, durable job broker, generic MCP network proxy or test of SSH/Work/CAC. Cloudflare Quick Tunnel supports the fixed nonstreaming JSON POST flow; no SSE. A second repository's composite-action invocation remains a separate test. No bridge from ChatGPT to this MCP is established.

**Admission:** source-safe public CI only; no private Actions minutes, user passwords, API/provider billing, local sovereign infrastructure, production network/host exposure, merges or releases.

**First-failure policy:** CI must preserve checksum, artifact sequencing, key decrypt, HTTPS, protocol and cleanup failures individually. Do not mark only one passing test as a full acceptance.
