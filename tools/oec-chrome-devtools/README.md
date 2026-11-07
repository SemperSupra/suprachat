# OEC Chrome DevTools MCP synthetic witness

Disposable independent browser witness using upstream `chrome-devtools-mcp@1.12.0` and an MCP SDK client (not a mock). Runs only against an original, anonymous local synthetic fixture. Collects a fresh accessibility snapshot, detects a fixture-only HTTP fetch, and observes a deliberate console warning. A performance trace is attempted if available and treated separately from core acceptance.

`--headless --isolated --no-usage-statistics --no-performance-crux` avoids telemetry and real-user Chrome UX Report requests. Never attach to a logged-in Chrome profile, fetch private ChatGPT conversations, or expose raw DevTools control over public Quick Tunnel. The evidence artifact contains counts and PASS/UNKNOWN classes, not request payloads, cookies, browser storage, or user data.

Next gate: explicit Playwright vs DevTools evidence comparison on the *same synthetic revision*, independently re-read the artifact and prove no side effects. UI/UX/DX audiences include human with assistive tech, automation, agent. No merge/release without separate authority.
