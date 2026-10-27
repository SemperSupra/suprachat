# Windows issuer and directory custody research — issue #51

Stacked on #62. This packet adds a **disposable synthetic custody mode** to the enrolled-peer/session
fixture. It uses fresh test signing keys, not existing credentials, browser state or user authority.
Existing product GUI/CLI/agent behavior and the default 17-case metadata fixture are preserved.
There is no production issuer, human enrollment or signed-in ChatGPT entry point.

## Boundary implemented

Each root is created once under a fresh random name with its owner/SYSTEM DACL present at creation.
Inheritance from its parent is disabled. Both allowed ACEs must grant full control with exactly
ObjectInherit and ContainerInherit; extra principals, missing inheritance and other ACE forms fail closed.
Directory handles are held without delete sharing. The W9 store holds its own directory lease for its
lifetime rather than depending on the fixture parent. Existing database, WAL, SHM and owner-lock
objects are checked on admission/load/save; uncertain custody during Save reaches the same fail-stop
hook as uncertain checkpoint writes. Session mutation/checkpoint authority remains unchanged.
File creation and checkpoint saves use a synchronous scoped duplicate token whose default owner is
explicitly the current user. Identity/privileges and the process token are not changed. This avoids
accepting a group-owned object when the host's normal token has a group default owner. Pipe checks
run outside that scope: the pinned .NET RunAsClient implementation reverts to the process context.
Tests verify restoration of the original token context after scoped filesystem work.

Issuer bootstrap is allowed only on the newly created root, uses atomic CreateNew and never falls
back to regeneration. A bounded DPAPI CurrentUser record contains a random 256-bit synthetic key,
version and random identifier. Additional public entropy binds the record to the root's random name.
Plaintext buffers are zeroed after protection/unprotection and issuer disposal. A missing, corrupt,
oversized, multiply linked or improperly permissioned record is refused before issuing grants.

Before writing or decrypting the record, the opened file handle is checked for type, reparse state,
link count, final path, owner and closed ACL policy. Ancestor reparse points and junction roots are
refused. Reopening checks the stored key; it does not silently install a replacement trust root.
The reopened issuer validates the original scoped fixture grant. A fresh child host independently
reopens DPAPI and returns only a fixed-domain proof over redirected private output; the parent
consumes it without forwarding it to console, receipts or artifacts. It is not a grant-signing endpoint.

The 17 real peer/session/checkpoint cases run with this protected-store mode and persisted test
issuer. Children still receive no signing key, grant, renewable credential or protected record.
Checkpoint crashes and restart/replay retain the selected W9 semantics.

## Windows proving cases

Fifteen additional cases cover protected root/key roundtrip, scoped file-owner restoration, reopened scoped-grant validation,
fresh-host DPAPI reopen, widened root/store refusal, stripped inheritance refusal, widened key ACL
refusal, corrupt/oversized/missing-key refusal with no reset, actual key hard-link refusal, actual
junction-root refusal, restricted-token positive control, key-open denial and directory-create denial.

The restricted token retains the owner's identity but adds a World restricting SID and disables maximum
privileges. Actual kernel opens succeed on a separate world-readable zero-filled control fixture and
fail with ACCESS_DENIED on custody objects. No Windows account is created or changed. This is
**restricted-token denial**, not an actual second-account or remote-user proving rep.

Tamper cases require precise refusal reasons and restore from raw ACL bytes with the .NET modification
flags set, then positively reload the protected key. Earlier incomplete receipts are retained: the first
test harness restored an unchanged ACL object, which did not persist the intended restore; later tests
could therefore refuse for the wrong reason. Exact-reason and positive recovery checks prevent that
false-positive pattern. An intermediate oversize test also needed to recognize InvalidDataException.
The first public Windows run 40083288542 refused object ownership before any peer mutation. Its
sanitized failure is preserved; creation now sets the user default owner on a duplicate token instead
of broadening the accepted object-owner policy.

```powershell
dotnet run --project SupraChat.PeerLedgerRdte -c Release -- --custody-receipt "$env:TEMP/fresh-custody-receipt.json"
```

Only booleans, closed labels, scope and counts enter the receipt. Keys, protected records, private
restart proofs, SIDs, names, process IDs and filesystem paths are not exported. Public/free Windows
CI preserves only that sanitized receipt; disposable issuer/SQLite files stay local to their runner.
The ordinary 17-case fixture and full tri-OS product workflow remain required regressions.

## Admission still withheld

The filesystem checks use pathname-opened handles; SQLite opens its own DB/WAL/SHM handles.
Their identity is not cryptographically or atomically bound to those verified handles. Retained root
handles and ACLs support the tested restricted-token boundary, **not general path-race resistance**.
Ancestor replacement, same-owner tampering, administrators/SYSTEM, rollback, hostile process races,
actual second-account denial, key rotation and production issuer enrollment remain unqualified.
DPAPI CurrentUser does not isolate other processes running as the same user. No human approval
is inferred from a Windows account or successful decryption.

This is a prerequisite research packet, not production custody admission. Shared desktop host/control
composition, durable provider-effect receipts, a reviewed metadata-consent flow and explicit operator
opt-in still precede signed-in HIL. No credentials, provider requests, profile export, main merge,
release, paid resource or modification to existing user directories/accounts occurs.

Primary references: [DPAPI scope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope?view=net-10.0),
[restricted-token access checks](https://learn.microsoft.com/en-us/windows/win32/secauthz/restricted-tokens),
[handle security descriptors](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo),
[ACE inheritance](https://learn.microsoft.com/en-us/windows/win32/secauthz/ace-inheritance-rules) and
[reparse operations](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points-and-file-operations).
The scoped-owner correction follows [Windows object ownership](https://learn.microsoft.com/en-us/windows/win32/secauthz/owner-of-a-new-object)
and the [matching .NET pipe source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs).
