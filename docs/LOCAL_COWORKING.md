# Local actor coworking

SupraChat uses sibling public and private repositories so a local actor can work with product code and durable private context without mixing their authority.

## Workspace

Default root:

`$HOME/Projects/SemperSupra/SupraChat`

Everything SupraChat-specific lives below that root:

- `suprachat/` — canonical public product/source/CI
- `suprachat-private/` — private DLE/oracle/evidence
- `_suprachat-cowork-context/` — local exported issue context
- `SUPRACHAT-COWORK.md` — generated workspace receipt
- `SUPRACHAT-LOCAL-ACTOR.txt` — generated local-actor resume baton

## Prepare or reconcile

Windows PowerShell 5.1 or PowerShell 7:

```powershell
./tools/local/Enter-SupraChatCowork.ps1 -Mode Plan
./tools/local/Enter-SupraChatCowork.ps1 -Mode Apply
```

The script uses SSH Git remotes, preserves dirty or non-default existing worktrees, fetches current remote state, and exports durable issue context when GitHub CLI is available.

It does not reset, clean, rebase, or force-check out local work.

## Actor start

Start the local actor in the public `suprachat` working tree and give it the generated `SUPRACHAT-LOCAL-ACTOR.txt` baton. The actor must read both repositories' `AGENTS.md` files and reconcile live GitHub state before acting.

Private DLE issue state, not chat reconstruction, is the continuation authority.

Human SIWC/OAuth approval remains a human boundary. Do not copy tokens, cookies, authorization codes, browser-profile secrets, or credential material into repository state, issue comments, or CI artifacts.

## Windows case-insensitive path collision

On Windows, `SupraChat` and `suprachat` resolve to the same filesystem path. If a legacy top-level checkout still exists at `$HOME/Projects/SemperSupra/suprachat`, rename or relocate it before creating the dedicated `$HOME/Projects/SemperSupra/SupraChat` workspace root.

The bootstrap scripts now refuse to continue when the intended workspace root itself contains a `.git` directory, preventing canonical repositories from being nested inside a legacy checkout.
