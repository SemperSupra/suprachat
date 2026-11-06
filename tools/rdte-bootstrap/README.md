# SupraChat: stable local RDTE bootstrap (candidate)

This is the proposed one-time `Start-SupraChat-RDTE.ps1` launcher, **not** a qualified release or replacement for the older V3–V6 user scripts. It is intentionally held behind the native Windows PS5.1 CI gate in this PR.

## Goal

Avoid repeat ZIP extraction/checksum/copy/run cycles. Source-only code, repo reconciliation, and native qualification stay in public free-tier GHA; a local Windows run occurs only when actual authenticated/device HIL is necessary.

The launcher uses Windows-included PowerShell 5.1 and either the operator's **already authenticated** `gh` or an existing process-local `GH_TOKEN`/`GITHUB_TOKEN`. It does **not** install any tool, configure credentials, mutate `PATH`, write to Windows profiles, touch project worktrees, or launch a browser. Do not infer that a runner can access the operator's private DLE from a public build.

## Promotion contract

The authoritative private owner issue is `SemperSupra/suprachat-private#2`. The launcher admits only a first-line exact `SUPRACHAT-RDTE-PROMOTION/V1` envelope **authored by the designated repository owner identity**, containing:

```text
SUPRACHAT-RDTE-PROMOTION/V1

Status: ADMITTED
SourceSHA: <exact 40 lowercase Git commit SHA>
AssetPath: tools/rdte-package/Invoke-SupraChat-RDTE.ps1
AssetSHA256: <exact 64 lowercase SHA-256>
```

The latest owner-authored promotion wins; `REVOKED` fails closed. Paths are allowlisted; the URL is derived solely from `raw.githubusercontent.com/SemperSupra/suprachat/<SourceSHA>/<AssetPath>`, never injected from DLE text. No actual promotion comment or release asset has been created in this PR. An illustrative contract is not admission.

A green CI run is a necessary gate, not unilateral permission to create a production promotion. Promotion requires qualified full-driver source, exact digest, and owner admission, with existing HIL and release holds enforced.

## Run protocol

`Plan` is read-only; `Apply` creates only a uniquely owned slot under `$HOME\.local\share\suprachat-rdte\packages\<RunLabel>`, downloads the exact SHA source, checks SHA-256, and records immutable owner/ready state. Repeating `Apply` on an identical, unmodified READY slot is NOOP; interrupted/ambiguous state is not blindly retried. `Verify` rehashes locally; `Run` executes only the verified artifact with an allowlisted mode in a child PowerShell process; `Undo -ConfirmUndo` removes only an exact owner-marked slot after reparse/foreign-content checks. No system installation or global changes.

The candidate is source/test only. Native PS5.1 tests use injected in-process fake downloads and synthetic private DLE comments. No live account or private token is projected onto public GHA.

## Security / unfinished qualification

- The bootstrap is only one component. A fully tested **target** RDTE package is required separately before any live local promotion, including native parsing, file protection, REST readback, restart/undo, concurrency, and local-profile isolation.
- This initial native suite checks the bootstrap's synthetic owner parsing, revocation, immutable URL and hash, idempotent install, tamper refusal, interrupted recovery, and bounded cleanup.
- Tokens remain supplied by the operator's own environment/authenticated `gh`, never as CLI arguments or DLE receipts.
- `Run` is not a general sandbox; do not launch account-authenticated clients or treat a successful child exit as semantic DLE acceptance.
- Preserve #153/#169 merge refusals, #152 UIA first failure and native writer lease, crash-journal create refusal, and Model B FUTURE. No PR/branch housekeeping mutations are implied by the launcher.

## Storage/handoff

Git stores source, deterministic fixtures, CI, accepted decisions and DLE evidence links. Large raw logs stay in isolated owner custody and are not uploaded into public Actions; the DLE holds sanitized fixed evidence and next executable baton. The human is not the message bus.
