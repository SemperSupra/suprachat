# Read-only GitHub PR hygiene census — no automatic housekeeping

Source-only Go 1.23 stdlib tool to classify up to 99 currently open public
PRs against one explicitly verified `main` SHA.

It compares each PR's changed-file head blob SHA with the same path on main.
The only potential safe fast lane is **EXACT_BLOBS_MANUAL_SUPERSESSION_REVIEW**.
Even then the owner must read current DLE authority and verify behavior, CI,
stack dependencies and prior refusals before closing. A nonmatching SHA is
`DIVERGENT_SOURCE_REVIEW`; novel files remain
`UNIQUE_SOURCE_PRESERVE`. Renames and paginated lists remain held.

**It never merges, closes, deletes, updates, comments, creates a branch, writes
to GitHub or prints tokens.** GET-only GitHub requests are bounded to 2 MiB
per response, at most 99 PRs/99 changed files per PR, 3-minute total and
explicit main SHA. Unknown API responses and incomplete data fail closed.

Public GHA qualifies the algorithm with synthetic local HTTP fixtures on three
platforms. The optional, explicitly dispatched live read-only audit job uses
GitHub's ephemeral `GITHUB_TOKEN` with `contents:read` and
`pull-requests:read`, uploads a redacted PR number/title/source fingerprint
report only. It **does not run continuously** or change repository state.

Custody: source and synthetic reports in Git/GHA; decisions and accepted
closures in private SupraChat DLE; no account keys, private worktrees, browser
profiles or remote client details here.
