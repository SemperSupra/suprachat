# OEC Disposable Mirror RDTE — v0.1

An isolated, no-credential public GitHub Actions synthetic experiment, **not a ChatGPT internal introspection tool**.

A disposable runner observes its own OS/architecture/toolchain, executes synthetic agent capability and UI-event fixtures, and tests claims against observable results. The runner does not directly see any hidden ChatGPT state, model weights, private context assembly, or the real user's authenticated ChatGPT session. A future supported-agent interface must deliberately provide a **sanitized, consented, source-pinned** metadata manifest, then an independent observer can compare outcomes.

## Local qualification

    cd tools/oec-mirror
    go test ./... -count=1
    go run ./cmd/oecmirror -input fixtures/synthetic.json
    go run ./cmd/oecmirror -runner-probe

Strict typed metadata-only manifest. Unknown fields, stale epochs, unregistered tool claims, duplicates, malformed revisions, oversized bodies, and synthetic-to-external completion promotion fail closed. The runner probe is DIRECT_RUNNER, never DIRECT_CHATGPT. The output is deterministic by input bytes and explicitly records unobserved capabilities and unknown boundaries.

This workflow uses public-safe fixtures only; no private repo checkout, API keys, provider network, authenticated browser session, Work execution, or external state writes. The next independently admitted step is a fixture-only Playwright/DevTools adapter and supported agent-to-runner metadata bridge; native WebView2/UIA or real ChatGPT session work requires a separate owner gate.
