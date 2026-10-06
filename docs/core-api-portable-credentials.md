# SupraChat Core API and portable SIWC credentials

Status: bounded implementation mission tracked by public issue #7.

## Decision

SupraChat is evolving toward one semantic Core API consumed by the native UI, automation CLI/JSON, agent JSON-RPC, and later SPA/PWA, Colab, and GHA clients.

This is not a wholesale rewrite. Existing working capabilities remain the implementations behind the core.

## Current tranche

Implement now:

- one `SupraChatCore` facade for authorization status, usable-credential acquisition, model discovery, and portable SIWC credential operations;
- cross-process credential refresh serialization on one host;
- monotonic credential-generation metadata so stale refresh results cannot overwrite newer rotating credentials;
- preservation of the upstream `earliest_refresh_at` field as opaque metadata until its public type/semantics are explicitly documented;
- encrypted, versioned portable credential bundles;
- explicit copy semantics for portable bundles;
- destination host identity preservation on import;
- equivalent human, automation, and agent-facing operations;
- UI/UX/DX cleanup around account and credential management.

Defer:

- remote credential broker / lease service;
- hosted multi-user service;
- generic provider framework;
- SPA/PWA replacement;
- Colab and GHA consumers.

## Credential bundle

Schema: `suprachat-siwc-credential-bundle/v1`.

The bundle is encrypted using AES-256-GCM with a key derived from a user-supplied passphrase using PBKDF2-SHA256. The JSON envelope exposes only redacted metadata sufficient to identify and inspect a bundle without decrypting its tokens.

Secret-bearing operations write bundle bytes to a file. CLI and agent receipts never return access, refresh, or ID tokens.

The first qualified mode is `copy`. A copy creates another holder of renewable authority. Because refresh tokens rotate, independent copies can invalidate each other's refresh lineage if both refresh. This tranche therefore records credential generations and prevents a stale local generation from overwriting a newer one, but it does not claim distributed multi-owner refresh safety.

The later broker/lease tranche will provide safe simultaneous ephemeral consumption without distributing the rotating refresh token.

## Host identity

Host identity belongs to the runtime receiving the credential. Import preserves the destination host's existing host ID rather than replacing it with the source host ID carried in the credential payload.

## Idempotency boundary

State-machine/idempotency machinery is applied only where current evidence requires it:

- credential refresh and generation replacement;
- credential import/export state transitions.

Ordinary reads remain ordinary reads. Inference operation IDs are deferred until a later execution/session tranche.

## Audience contract

Human:
- account and authorization status;
- protected export/import controls;
- clear warning that a bundle contains renewable authority.

Automation:
- JSON-emitting `auth-status`, `auth-bundle-inspect`, `auth-export`, and `auth-import`;
- passphrases supplied through environment variables, not command-line literals;
- secret bundle contents never written to stdout.

Agent:
- semantic `auth/status`, `auth/bundle/inspect`, `auth/export`, and `auth/import` JSON-RPC methods;
- export/import require explicit confirmation;
- passphrase value is supplied only for the requested operation and is never returned.

## Qualification

This tranche must remain green across Windows, Linux, and macOS. Tests must prove:

- bundle encrypt/decrypt round trip;
- wrong passphrase fails;
- bundle envelope/receipt contains no token material;
- imported host identity is local;
- credential generation advances on refresh;
- stale generation replacement is rejected;
- UI automation IDs remain present for new controls;
- automation and agent methods remain semantic and machine-readable.
