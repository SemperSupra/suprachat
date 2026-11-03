# Ephemeral Agent Observatory — synthetic capability passport

This small Go tool is the first **evidence-driven documentation layer** for the disposable observatory. Its three source receipts are downloaded from specific, already qualified public GitHub Actions runs, not copied from chat text.

- MCP Inspector v2.13.0: run `41661531502`, draft PR #147, commit `4791b5910394bc07cf4628eb1d3abce512d2190b`.
- Chrome DevTools MCP v1.12.0: run `41662816944`, draft PR #148, commit `83301fec49a004ef9ddbe9d8580629eab67fa074`.
- OpenTelemetry Collector v0.143.0: run `41661990184`, draft PR #149, commit `144be27740c09e84d8693bcd8e2eab3b6d24a637`.

Every input JSON must match an explicit field inventory and support its advertised evidence class. Evidence that claims real ChatGPT access, broad browser access, user content, tokens, or missing independent tests is refused. The output includes the SHA-256 of each **extracted receipt file** (not the ZIP archive), exact source run and Git SHA, verified synthetic classes, remaining UNKNOWNs, and a synthetic-only status. It is deterministic and contains no generated timestamps.

The public workflow uses only `actions:read` to download artifacts from the same public repository; artifacts expire and the job must fail closed on missing or changed receipts. All three underlying PRs remain draft and unmerged. This tool does not give this ChatGPT session direct access to browser or MCP servers. It does not instrument hidden model internals or replace the project-native authoritative private DLE.

**Storage:** public Git holds original source/tests/POA&M; run artifacts hold redacted evidence; private DLE #2 records acceptance and next baton. Large rights-cleared binaries, if needed later, use private Drive with independent custody. Do not merge, release or deploy without owner gate.
