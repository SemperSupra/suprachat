# Model A installer admission — planner → official tunnel handoff

This is a bounded, **non-executable** integration between the already merged
`tools/suprachat-provision` ownership planner and the already merged
`tools/oec-tunnel-bootstrap` official tunnel runtime wrapper.

It is NOT a ChatGPT plugin installer, credentials manager, cloud account
provisioner, full uninstall executor, or replacement for the held crash-safe
journal branch.

## Invariants

1. One independently owned tunnel, runtime and ChatGPT connection **per installation**, all in exactly one workspace scope. Two devices may share a workspace only with distinct connection identities and remote tunnel IDs.
2. Use the official tunnel client's **single managed connect** for create/reuse; never independently issue one tunnel create plus another create-through-connect.
3. The source plan may say an owned remote tunnel is idempotent, but this admission requires both recorded and newly observed **matching** owner fingerprints before producing a reuse handoff.
4. All emitted commands are inert: no `--execute`, no official binary path, no key values, no account calls. Only environment-variable *names* appear.
5. Operator-approved live ChatGPT plugin registration, tool discovery, read-only call and account disconnection remain separate explicit gates. An installed Codex plugin is not a ChatGPT account app.
6. **Uninstall refuses to stop even a local runtime** if the ChatGPT connection is still attached, owner/fingerprint is uncertain, or the remote tunnel deletion adapter plus write-ahead journal is unqualified. No partial removal.
7. Upgrade remains blocked until the owner-scoped durable journal, version migration and independent rollback verification are accepted.
8. No Model B shared gateway, shared poller, opaque credential delegation, user profile access or release.

## Test and evidence

`go test -shuffle=on -count=5 ./...`; `go vet ./...`.
The public workflow tests Windows 2025, macOS 26, Ubuntu 24.04 and Linux race,
including two concurrent distinct installations, same-scope remote collisions,
wrong ownership, missing fingerprint, attached plugin, version upgrade hold,
untrusted endpoint and secret fields. Successful synthetic plans explicitly say
`dry_run=true`, `changes_applied=false`,
`chatgpt_plugin_installed=false`, and
`authenticated_chatgpt_verified=false`.

### Storage

This source, schema, tests, and redacted synthetic evidence belong in public Git.
The owning private SupraChat DLE records first failures and acceptance.
Installed object IDs and OS-vault credential references belong in protected
local app data, not the public repo or GitHub Actions artifacts.
