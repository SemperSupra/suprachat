# Shared desktop / automation / agent workbench — issue #51

Stacked on #63. This packet composes the selected ledger, protected W9 checkpoint, synthetic issuer
and bounded trace reader behind a real accessible desktop window and two enrolled semantic clients.
It is an isolated research entry point in the RDTE executable. It never constructs the normal product
MainWindow or a WebView, opens a signed-in profile, or performs a provider operation.

## One authority and three surfaces

The host owns exactly one SharedSessionLedger and one W9 checkpoint. The GUI holds a bounded
read projection and submits its displayed version as CAS; it does not keep a second mutation ledger.
Its local owner-fixture grant uses the automation audience. A UI control is not proof of a human or
human approval. The app visibly labels the workbench synthetic and metadata-only.

The host launches one automation child and one agent child concurrently, enrolling their actual
process instances. Each has its own parent-kept actor/audience/session grant. A bounded initial hello
is read and the OS PID/start-time/token subject checked before the welcome sends synthetic IDs.
Every subsequent command checks the same peer again inside ledger admission. Unknown request
members are refused. There is no arbitrary external-client enrollment or public grant endpoint.

The automation client uses operation/params frames. The agent uses JSON-RPC2.0 method/params/id
requests and matching jsonrpc/id/result responses, with version and ID validation in the child.
Both use the same bounded named-pipe transport and map to the same core operations. Semantic
authorization denials are result receipts; this fixed fixture is not a general-purpose JSON-RPC server.
No automation or agent operation scrapes the GUI. Only the independent UIA qualification acts on
the isolated window to verify accessible human controls and their resulting projections.

Reads copy trace metadata while the existing ledger admission/revocation gate is held. Each page and
event must carry the authoritative session ID for its held trace capability. Two sessions retain distinct
capabilities and independent revisions. Request/grant IDs alone cannot become a trace capability.
Recorder observations remain separate from durable session state; no durable trace recovery is claimed.

## Qualification sequence

The real UIA runner changes session A's title through its accessible value/invoke controls. Automation
and agent children then inspect and mutate A/B concurrently, read sanitized traces and request the
other session as an adversarial case. Both wrong-session requests must be refused. The GUI refreshes
and reads those same authoritative titles/versions, including session B through its selection control.

A newly enrolled agent process then self-revokes through the same grant and must receive a refusal
on its next read. Once all workers finish, the checkpoint is closed/reopened and metadata compared.
The recovered revoked grant is submitted to the guarded projection API and must never invoke the
copy callback. The owner-fixture can still read its own grants. Expected final versions are A2 and B1,
with one common ledger cursor and one correctly routed synthetic trace event per session.

The host receipt and separate UIA receipt exclude titles, prompts, grants, protected keys, local paths,
OS identity/process values and private proofs. Workers have deadlines and own-child cleanup; closing
the window cancels requests and drains workers before disposing authority. A deadline or incomplete
workflow fails qualification. Only sanitized receipts enter public/free Windows CI.

```powershell
dotnet build SupraChat.PeerLedgerRdte -c Release
./tools/local/Test-SharedDesktopLab.ps1 -ReceiptPath "$env:TEMP/fresh-shared-desktop-receipt.json"
```

Local fresh builds and actual UIA runs passed. Review found that the first implementation sent bare
agent replies despite using JSON-RPC requests; the repaired version sends and verifies complete reply
envelopes. Independent exact-head review, Windows shared-desktop qualification, existing custody and
default peer fixtures, and full tri-OS product regression remain required before acceptance.

## Remaining admission

This proves disposable shared metadata composition, not integration of production CLI/agent services,
human authentication, arbitrary external actors or shared provider cancellation/effect receipts.
Production custody, hostile owner/admin/path races, actual second-account denial and rollback remain
gated as documented in WINDOWS_OWNER_CUSTODY.md. A reviewed per-session metadata consent flow
and explicit operator opt-in still precede signed-in Windows HIL. Provider payload capture and first-party
traffic modification remain unavailable. No main merge, release or paid resource is authorized here.
