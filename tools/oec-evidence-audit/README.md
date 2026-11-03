# Source-pinned public synthetic artifact custody — OEC

Independent public Actions workcell for **actual ZIP-byte hash/readback** rather than relying solely on GitHub upload metadata or the PR author's summary. Acquires exactly four original source-safe Actions artifacts, verifies the corresponding workflow run commit SHA via read-only GitHub API, downloads full ZIP archives into ephemeral runner storage, compares the uploader-reported archive SHA-256 to independently computed bytes, validates one bounded JSON receipt and its exact schema, recursively rejects private-content markers and unexpected credential/account claims, and emits only a sanitized SHA-indexed audit receipt.

Targets: Inspector #147, Chrome DevTools #148, OpenTelemetry #149, integrated cross-witness draft #153. Exact immutable source tuples are in `manifest.json`, not dynamic "latest" searches.

**Adversarial tests:** replay/substitution of ZIP hash, forged higher authorization claims, nested JSON bearer, unexpected archive files and ZIP path traversal all fail closed.

**Limitations:** Public source remains synthetic only. Successfully hash-matching a ZIP proves custody of that published Actions artifact, *not* model runtime introspection, independent upstream provider attestation, or proof that a private user session was observed. Original CI artifacts expire under their configured retention; this repository retains manifest and per-run verified digest metadata, **not the actual old ZIP bytes**. To support long-lived byte custody, store only the small rights-cleared canonical JSON receipts in Git or a separately admitted Drive location before expiration.

This workcell is isolated to `tools/oec-evidence-audit/**` and its own public workflow, with `contents:read` and `actions:read` only; no private repository, secrets, production credentials, release, deployment or protected merges. Human/automation/agent audiences get a concise README and fixed machine-readable `oec-evidence-zip-audit/v1` result.
