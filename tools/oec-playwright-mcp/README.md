# Ephemeral Playwright MCP browser witness (public and synthetic only)

This public-safe, disposable GitHub Actions experiment uses Microsoft's genuine `@playwright/mcp` package **0.0.84**, with an MCP SDK client that invokes named tools through the official protocol rather than bypassing it with standalone Playwright calls.

## Scope and operation

- Run official Playwright MCP in an isolated, headless browser inside one public Ubuntu Actions job.
- Start a small original synthetic conversation interface at a random loopback port; make a fresh accessibility snapshot, observe a prompt control, type fixture-only text, click a synthetic submit button, verify the resulting status, toggle a test panel, inspect mobile viewport, and optionally capture a screenshot. These interactions all occur through MCP tools.
- Visit **only** the public signed-out `https://chatgpt.com/` landing page. The public probe is passive. Cloudflare/interstitial/robots or network blocking is classified as unknown or unavailable, never bypassed. No sign-in, saved browser profiles, user prompts, cookies, account access, paid APIs, protected content, browser extensions or multi-user sessions.
- Write `artifacts/receipt.json` containing classifications, counts, tool names and evidence tiers, **not raw site content**. Upload an original synthetic screenshot only. The runner and isolated browser expire.
- This is a *preauthored interactive MCP tool loop*, not a live session in which this ChatGPT chat can issue new commands to the runner. Connecting this chat to remote tools would require a separately authorized client/plugin and stable supported transport. **Never expose raw Playwright MCP through a public Quick Tunnel**; use a separately reviewed restricted witness/proxy if remote control is later admitted.

## Why this upstream

Microsoft's Playwright MCP provides `browser_navigate`, `browser_snapshot`, `browser_type`, `browser_click`, viewport controls and screenshots with structured accessibility references. We reuse upstream instead of building a bespoke browser driver.

## Safety boundaries

The scripted investigator treats browser content as untrusted data, not operational instructions. Its control policy has a fixed domain allowlist: localhost fixture and the signed-out public ChatGPT homepage only. Navigation failure on the public site is recorded without retries or bypass. No user data, session state or URLs containing secrets are exported. GitHub public Actions and artifacts are intentionally treated as public.

## Qualification / follow-up

Initial acceptance requires: official MCP initialization and tool discovery, synthetic before/after state verification, authenticated-content exclusion, redacted evidence, and an actual Ubuntu public runner receipt. A separate release gate would add a constrained read-only external observer over a tunnel; platform-specific browser HIL and authenticated account inspection are not part of this prototype. Keep source and verified DLE evidence in Git/private DLE; no private runners, merges, releases or deployments.
