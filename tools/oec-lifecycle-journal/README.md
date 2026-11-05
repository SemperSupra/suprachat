# Model A — owner-scoped crash-recoverable installation journal

This Go standard-library transaction core journals local SupraChat install and uninstall effects. It does **not** contact OpenAI, create a real tunnel, install a ChatGPT custom plugin, read browser state, or store credentials.

Each installation has a distinct local journal, exclusive process lock, normalized IDs, resource scope and owner, and allowed operation/undo pairs. It writes `prepared` to disk before creating a resource and requires independent observed proof before writing `verified`. On interrupted creation it observes the resource; if uncertain, it refuses to repeat the action.

Uninstallation reverses only owner-verified work. Before deleting a tunnel, the future real adapter must independently attest that its ChatGPT connection is detached; otherwise `SafeToUndo` refuses. It persists `undo_pending` before deletion and, after an interrupted operation, observes absence rather than repeating it. Removed records remain as tombstones.

Three audiences: humans receive a nonsecret status summary; automation uses machine-readable JSON journal entries; agents can reconcile exact scope, ownership and first failure. The journal must reside in user-local protected app-data; OAuth/Platform credentials stay in an OS credential vault, never the journal.

**Tested:** two installations in one workspace; idempotent install/delete; interrupted create, missing observation, interrupted delete, foreign owner/drift, unattended ChatGPT plugin denial, stale lock and corrupt journal. These are synthetic adapter oracles, not authorization to deploy.

**Still required:** bounded adapter to merged official tunnel bootstrap, real plan-to-intent conversion, versioned upgrade/rollback semantics and migration, OS-specific owner-only state handling, authorized ChatGPT registration/detachment confirmation, and authenticated end-to-end HIL. Model B managed gateway is deferred.

Run `go vet ./...` and `go test -race -shuffle=on -count=3 ./...` from this module.
