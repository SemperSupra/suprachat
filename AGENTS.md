# SupraChat actor contract

This repository is the public product/source/CI authority for SupraChat.

First-class audiences:
- humans through native desktop and web-facing product surfaces;
- automation through stable CLI, lifecycle, diagnostics, and machine-readable receipts;
- agents through semantic JSON-RPC/app-server/protocol surfaces.

Do not make one audience depend on scraping or wrapping another audience's interface.

Working rules:
- preserve human UI/UX, automation DX, and agent DX together;
- preserve accessible semantic controls and credential boundaries;
- keep qualification on public/free GitHub Actions;
- never place credentials, cookies, authorization codes, or token material in repository state or CI artifacts;
- current bounded architecture/credential-portability mission: public issue #7;
- current bounded workstreams: #19 (UIA qualification), #38/#39 (control research), and #51 (native metadata lifecycle); consult each issue's current acceptance gates before extending scope;
- current private DLE/oracle/evidence continuation: SemperSupra/suprachat-private#2;
- closed public dogfood issue #4 and closed private issue #1 are provenance, not active authority;
- start further product work as a new bounded mission rather than silently extending a closed one;
- use Agent Dispatch only as execution/orchestration substrate, not as product authority.

## R48 default execution and cold-start authority

Read [`docs/PARALLEL_WORKING.md`](docs/PARALLEL_WORKING.md) and the latest private [DLE #2](https://github.com/SemperSupra/suprachat-private/issues/2) before making a work plan. The durable private `worksets/parallel-batches-r48.json` (on its reviewed proposal branch until integrated) contains the current bounded parallel assignments. Prefer **two to four independent READY batches** per iteration with disjoint write paths, unique branches, separate oracles, and free public CI; never default to one task at a time when more are safely READY. Respect sequential admission/integration gates and the owner-reserved merge/release boundary. Distinguish public GHA matrix concurrency from actual independent workstreams.

Use small, bounded instructions for weaker local models; read the repository instructions and workset directly rather than relying on pasted chat context. Existing `tools/local/Enter-SupraChatCowork.ps1` emits the local baton. Every batch closes with exact PR/head/CI and evidence DLE reconciliation. An authenticated human session owner's intent and stop/pause request takes priority and fences affected actor work; a UI role label does not establish identity. Maintain all three audiences and accessible human interfaces against one canonical session.
