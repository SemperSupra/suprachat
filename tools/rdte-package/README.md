# SupraChat model-free RDTE target — public-safe native candidate

This directory is the **fixed allowlisted target artifact path** consumed by the separately qualified stable launcher (public draft #172). It ported prior local V6 source into public-safe V7, preserving its observed native first failures and removing the one-time legacy V5 recovery and read-only UIA HIL lane. This is source/fixture qualification, not an admitted local deployment.

## Execution and custody

- Target file: `tools/rdte-package/Invoke-SupraChat-RDTE.ps1`.
- The stable bootstrap places the SHA256-verified target and immutable `owner.json` under an exclusively owned disposable package slot. The target reads the bootstrap owner and the locally configured private DLE reference; **neither private repo identity nor credentials are embedded in public source**.
- `SelfTest` and `LocalSmoke` are native PowerShell 5.1-only and do not require DLE/token or real user accounts. They execute in a disposable .local tree with exact owner cleanup.
- Subsequent `Plan`, `Apply`, `HygienePlan`, `HygieneApply`, `Verify`, `Publish` and `Undo` require current owner-authored `SUPRACHAT-RDTE-PROMOTION/V1` admission: source Git commit SHA + asset path + SHA256 must match the downloaded file and current bootstrap owner. Latest revocation stops action; duplicate/malformed first-line promotions fail closed.
- Routine synthetic/model-free qualification uses exact pinned public source snapshot, disposable .NET/Go locations/caches, bounded test commands and SHA256 local receipts. Source tests are not a security sandbox.
- Public PR census is read-only, candidate-only, with no branch creation, PR mutation, merge, closure, release or deployment.
- `Publish` posts only sanitized, exact-first-line evidence summaries and independently verifies complete payload readback; ambiguous POST requires reconciliation, never blind retry. The internal original V6 `$Matches` variable-collision is corrected.
- Historical V5 `RecoverV5` is **removed**: that remote receipt already exists and must never be reposted.

## Native qualification

`Test-Invoke-SupraChat-RDTE.ps1` uses fake GitHub API data, >200 synthetic comments, first-line promotion, revocation, duplicate/rejected markers, receipt conflicts, and actual PS5.1 native parser and local disposable file operations. It does **not** exercise real credentials, private DLE, authenticated ChatGPT desktop, any Cloudflare tunnel, real account sessions, release or promotion.

## Mandatory pre-deployment gates

- Exact PR head Windows PS5.1 native tests, isolated SDK and test command launch/cancellation, cleanup/timeout/oracle checks;
- Independent review of DLE transport and author identity, promotion revocation, source provenance, and encrypted/credential handling;
- Exact target file SHA-256 and source commit captured in a **separately owner-admitted** private DLE promotion;
- One-time stable bootstrap installation and genuine user-supervised HIL only after the above; no new ZIP/per-version local copying.

No production claim or local operator action is authorized merely by a green source workflow.

## Repository housekeeping

Keep source/fixtures/CI/runbook in public candidate PR, admission/receipts and authoritative baton in private owner DLE, private raw logs in owner custody. Do not publish account tokens, local infrastructure details, browsing histories, cookies or conversation content to public GHA. Existing #153/#169 scoped merge refusals, #152 native UIA lease, journal PR-creation refusal, and Model B future state remain unchanged.
