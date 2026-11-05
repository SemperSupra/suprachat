# Provider fault oracle (independent synthetic model)

Scope: source-neutral, stdlib-only adversarial **model** for planned SupraChat provider binding and telemetry semantics. No network, no provider SDK/API, no credential, no account, no scraping, no actual session ledger, no tool execution.

Run: `python tools/provider_rdte/fault_oracle.py` on any OS with Python 3.12+. The command emits a machine-readable `SYNTHETIC_MODEL_ONLY` summary and exits nonzero on failing cases.

The oracle intentionally tests identity and operation collision, ambiguous retry refusal, partial/fragmented/duplicate streams, sovereign data-transfer refusal, and positive-allowlist telemetry. It must **not** be cited as native API, authenticated WebView, production adapter, or three-audience integration proof. The runtime implementation must be tested separately against this independent reference behavior.

Public-safe and disjoint from existing browser/Workbench/credential workcells. For authority, see private SupraChat DLE #2 and multi-provider draft PR #10.
