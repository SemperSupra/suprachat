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
- current private DLE/oracle/evidence continuation: SemperSupra/suprachat-private#2;
- closed public dogfood issue #4 and closed private issue #1 are provenance, not active authority;
- start further product work as a new bounded mission rather than silently extending a closed one;
- use Agent Dispatch only as execution/orchestration substrate, not as product authority.
