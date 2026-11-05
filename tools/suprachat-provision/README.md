# SupraChat reversible provisioning — source-isolated proof

This Go module is a **pure dry-run planner** for the official OpenAI and ChatGPT resource lifecycle. It writes no account resources and never claims the ChatGPT connector has been installed.

Official upstreams: [OpenAI CLI](https://github.com/openai/openai-cli) for Platform project/user/service-account administration; [OpenAI tunnel-client](https://github.com/openai/tunnel-client) for admin tunnel CRUD and managed runtime connect/status/stop; [Terraform provider](https://github.com/openai/terraform-provider-openai) for declarative project resource patterns; [plugin packaging](https://developers.openai.com/plugins/build/plugins) for repo-local marketplace/portable plugin format. OpenAI API hosted `/v1/skills` creation/version/deletion is for API agent/shell workflows, **not** personal ChatGPT plugin installation.

## What is demonstrably reversible?

Every local instance gets an immutable `install_id`, owned resource IDs, scope and version; authoritative observed fingerprints prevent blind deletion. Owned tunnel deletion waits until any ChatGPT custom plugin attached in that scope is detached. Unmanaged/shared resource IDs are **preserved**, never silently adopted. Owned Platform projects are **archived** only with explicit approval, never treated as a general-purpose deletion. Hosted API skills are a separate resource kind. Upgrades retain a previous-version rollback action.

The installer will eventually supply a bounded executor for these plan operations through official APIs/CLIs; this tranche proves only the **planning and safe teardown predicates**. A real ChatGPT account plugin is currently an operator-authorized action, not an unattended public API call. `.agents/plugins/marketplace.json` exposes a *local* plugin in supported clients, but does not install or authorize the app.

## Reproduce with no keys or side effects

```sh
cd tools/suprachat-provision
go test -shuffle=on -count=5 ./...
go run ./cmd/plan --request fixtures/install.json
go run ./cmd/plan --request fixtures/uninstall.json
```

Full acceptance for installer integration additionally needs an authorized resource executor with capability-limited credentials, durable user-local transaction journal/rollback, crash and migration testing, packaged installer/repair/uninstall, verified ChatGPT custom plugin setup, and local HIL. Public CI must never contain account credentials, browser profiles or private data.
