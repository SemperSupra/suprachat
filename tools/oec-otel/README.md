# OEC OpenTelemetry Collector P0: metadata-only ephemeral trace pipeline

This public Actions workcell runs the **real OpenTelemetry Collector** Docker image `otel/opentelemetry-collector:0.143.0` with OTLP/HTTP receiver and debug exporter, bound to the runner loopback. A stdlib Python fixture submits two related spans with a shared trace identifier: one successful agent observation and one expected negative synthetic result.

The sender rejects unexpected attributes including prompt content, output, authorization material, user identifiers, and arbitrary strings outside the fixed allowlist. The collector output is checked for the synthetic span names, trace correlation, and absence of forbidden marker text, then discarded. Only a sanitized summary is uploaded.

This demonstrates **collector ingestion and trace correlation on runner-owned synthetic data**, not true ChatGPT internals, production tool instrumentation, independent provider attestation or full private-data non-leakage. A real service would need full egress and signal governance, retention limits and independently deployed redaction before receiving user content. No private repo checkout, native host, private runner, merge, release or deployment.

Jaeger UI and Phoenix are deferred until this minimum collector leg passes, to avoid adding services to the critical path. Later phase: attach actual authorized agent tool/span instrumenters and compare with browser observer receipts.
