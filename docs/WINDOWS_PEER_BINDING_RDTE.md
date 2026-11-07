# Windows OS peer-binding proving rep — issues #39 and #51

Independent from canonical main 250e5ef9d2a3017e8374d1a8243429abcc54eaf0. This standalone research
executable does not link the application, W6 host, credential store, browser, grant issuer or provider.
It proves a narrow Windows IPC identity primitive using actual local child processes.

## Observed identity versus claimed authorization

For every case, the parent creates a fresh NamedPipeServerStream with CurrentUserOnly and one
instance, then launches one bounded fixture child. The expected child PID and creation time are
enrolled by the parent from the process it created, not accepted from the pipe message.
GetNamedPipeClientProcessId supplies the actual connected peer PID. The matching process creation
time binds the expected instance rather than treating a bare numeric PID as a lasting identity.

After reading a bounded frame, RunAsClient exposes the client's Windows token for identification.
The fixture reads only its User SID and impersonation level in memory, compares the SID with the
owner, and observes an identification/impersonation token. The SID, account name, machine name,
process IDs, creation timestamps and handles are never exported. It performs no file operation while
impersonating, never requests elevation or a password, and never touches ChatGPT authorization.

The independently observed OS peer still does not authorize a caller-provided role. Only the role
enrolled for that exact fixture process may be admitted. This intentionally separates actual OS
identity, process instance, claimed PID and requested role. A same-user caller is not automatically
a human, administrator, agent or approved actor.

## Cases and bounds

Actual local cases: enrolled automation; enrolled agent; forged human role; forged PID; correct same-user
peer that was not enrolled; caller-provided actor subject; oversized frame declaration. Only the first
two are admitted to the fixed fixture. The others receive closed refusal reasons. Every connection has
a deadline and a 1024-byte frame limit; the parent kills only its own stuck child on failure. Rendezvous
names are fresh non-credential UUIDs. No bearer, signing key or renewable authority is passed as an
argument, environment variable or protocol message.

The first rep passed six identity/authorization cases but was incomplete at the oversized-frame
case. The corrected case sends the invalid length header without an oversized body, proving refusal
before allocation without relying on Windows pipe buffering. The first receipt is retained locally.
The corrected local rep passed all seven cases with actual OS process and subject observations.

```powershell
dotnet run --project SupraChat.WindowsPeerRdte/SupraChat.WindowsPeerRdte.csproj -c Release -- --receipt "$env:TEMP/fresh-peer-receipt.json"
```

Receipts contain booleans, closed case/reason labels and scope only. A privacy oracle rejects the
owner SID, machine name or synthetic private sentinel before writing a receipt. A dedicated public
Windows workflow executes the actual parent/child pipe rep and preserves only that sanitized receipt.

## Explicitly not qualified

This does not establish production trusted-issuer enrollment, signing-key custody, executable/model
attestation, proof of a human, another user's denial, remote admission, PID-reuse resistance under
hostile termination/races, arbitrary external clients or W6 integration. CurrentUserOnly is selected,
but a separate adversarial Windows ACL/cross-user proving rep is still required for production custody.
No production server, role grant endpoint, browser operation, credential export or provider action is
introduced. Public CI PASS is bounded OS-peer fixture evidence, not production actor admission.

Next: independent security/source review; qualify owner/issuer custody and real enrollment; bind that
proof to the selected W6 admission and #52 durable revocation inside the same mutation gate. Keep
human approval, OS subject and logical actor role distinct. No merge/release authority is implied.

Primary references: Microsoft's [GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
and [RunAsClient](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.runasclient?view=net-10.0).
