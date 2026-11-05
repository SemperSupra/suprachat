# Operator-side PowerShell 5.1 local RDTE driver — source custody manifest

**Status: pre-admission, not executable source custody.** The 34,846-byte driver is delivered in the initiating ChatGPT conversation as `Invoke-SupraChat-RDTE.ps1` and is not yet present in this branch. No cloud CI or Windows PowerShell parse qualification is claimed.

- Exact accepted baseline: `27460bd291d57072799f1ff70d5727de6392a9ed`
- Driver SHA-256: `c1e5194a2b16ac5235c5a2b624599b3368283ddbbd23e1ad0a6d7adf0c907a45`
- Owning private DLE kickoff/return contract: [SemperSupra/suprachat-private#2 comment 6665418601](https://github.com/SemperSupra/suprachat-private/issues/2#issuecomment-6665418601)
- Expected Windows operator placement: `$HOME\Downloads\Invoke-SupraChat-RDTE.ps1` (this remote agent cannot write the host's Downloads directory)

## Evidence authority and boundaries

The driver requires an exact private DLE kickoff marker before running; it posts only sanitized result status and SHA-256 hashes back to that same issue, then independently confirms the comment is durable. It downloads a public source ZIP at a fixed SHA into `$HOME\.local\share\suprachat-rdte`, runs Go/.NET tests with process-local caches, and can remove only its owner-marked disposable tree. Optional read-only UIA targets an explicitly selected existing SupraChat PID. It must not launch an authenticated WebView on the user's normal Windows profile because current `AppState.DirectoryPath` is hardcoded to `%LOCALAPPDATA%\SemperSupra\SupraChat`.

## Next custody baton

Operator should verify SHA-256 before first execution, then publish the exact bytes of the downloaded script into a source file on a reviewed, source-isolated branch, not directly on `main`. Obtain a real Windows PowerShell 5.1 parser/self-test pass and focused GitHub Actions qualification before merge or production use. Do not bypass refused PR #169 merge, crash-journal PR-create, or prior native #152 lease restrictions. Keep credentials and raw local test logs off GitHub.

No live ChatGPT login, MCP registration, release, or external deployment is authorized by this manifest.