# OEC Cloud Preflight — synthetic handoff qualification

**Scope:** Independently qualified public-safe preflight fixture, not a production authenticated bridge, browser plugin, local actor, or release. No private/DoD/browser profile/cookie/account state is used.

## Real effects exercised
- Go stdlib HTTPS loopback listener, client, signed one-use capability, short expiry, bounded body, no browser Origin.
- Read-only synthetic snapshot -> one explicitly scoped synthetic action -> independent readback -> replay rejection -> endpoint shutdown.
- Token substitution, generation mismatch, expiry, forged MAC, method/scope, body injection, duplicate JSON keys, unsafe operations, stale revisions, oversized requests and concurrent nonce replay.
- Sanitized versioned machine-readable evidence. Explicit authenticated_chatgpt=false and live_local_hil=false.
- Native Windows, macOS, Linux tests and Linux race detector on public free-tier Actions. Any failing required test blocks qualification.

## Local development
Run: go test -shuffle=on -count=5 ./...
Run: go run ./cmd/preflight > evidence/preflight.json
Run: go run ./cmd/verify evidence/preflight.json

The public workflow creates the temporary evidence directory and verifies receipts. No external modules, browsers, authenticated sessions or production interfaces are required by this fixture.

## Mandatory separate gates before operator HIL
C0: this exact branch three-OS cloud Go suite, race detector and independent receipt verifier PASS.
C1: existing cross-runner Cloudflare handoff #151/#155, replay/TTL, endpoint-substitution, cleanup and confidentiality negatives.
C2: actual upstream Inspector/Playwright/Chrome/OTLP synthetic causal tests #146/#153/#157 and privacy/negative coverage.
C3: native Windows UI Automation #152 root and semantic controls PASS in the protected full-product workflow.
C4: install/repair/uninstall, crash/restart, stale session, offline/firewall and supported-platform packaging from exact qualified source.
H0: first operator-owned local signed-in read-only ChatGPT observation; UNKNOWN until local run.
H1: owner-approved real UI action plus independent readback; UNKNOWN until H0 passes.

Cloud tests cannot guarantee compatibility with a real browser profile, login state, local security policy, extension or future ChatGPT UI. C0 is necessary, not sufficient. The operator HIL must be a short read-only smoke followed by narrowly permitted action and teardown, never a diagnostic discovery campaign.

## Custody
Public Git: source, synthetic fixtures, schemas and privacy-safe evidence. Private DLE: actual source SHAs, gate conclusions, first failures and next baton. Approved large nonsecret assets in restricted Drive with SHA index.
