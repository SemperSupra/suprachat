# SupraChat #19 — W3 provisional terminal lifecycle qualification

Status: **draft, review-only**. Source authority is public draft PR #32,
stacked onto the independently qualified W2 combined workbench PR #31.
This is **not** the interactive terminal product or a security sandbox.

## One process, one selected session

A human explicitly chooses an installed absolute `pwsh`/`pwsh.exe` path and
selects a collision-free workbench session before clicking **Start idle PowerShell child**.
The managed supervisor launches a new process for the selected session using
the fixed argument vector:

```text
-NoLogo -NoProfile -NonInteractive -Command -
```

Stdin remains redirected and open **inside the supervisor only** to keep the child idle.
Neither the UI nor automation surfaces expose input forwarding or arbitrary process arguments.
The process executes no user-supplied terminal command. Standard output and standard
error are **drained and discarded**; no terminal output is displayed or saved.
No PTY, terminal emulator, browser binding, privileged container, ChatGPT send,
or transferable execution authority is present.

The child inherits a reduced, allowlisted set of OS environment variables rather
than the host's entire environment. This decreases accidental credential inheritance
but does **not** prove confidentiality against an untrusted executable. A renamed or
tampered `pwsh` on an operator-specified path is *not* authenticated by a filename check;
operators must independently trust the binary provenance.

## Runtime governance and what is (not) enforced

| Guard | Provisional implementation | Assurance limitation |
| --- | --- | --- |
| Process ownership | one process per session | not a kernel namespace, OS job, or container |
| Concurrent processes | at most 2 | in-memory quota; only this supervisor |
| Lifetime | 90-second termination check | polling, not a scheduler-enforced hard deadline |
| Output | discard and kill after 32 KiB observed | async reads may race by a small bounded chunk |
| RSS | kill if observed working set exceeds 256 MiB | **not** a hard RAM allocation or reservation |
| CPU/disk/network | none | **no CPU quota, disk isolation or network sandbox** |
| Unknown child exit | fail closed; retain ownership/state | independent OS inspection needed |
| Close | stop child first, then forget session | fail closed if exit was not verified |
| Application shutdown | best-effort kill process tree | crash/OS-level kill may prevent cleanup |

No Docker Desktop/Engine or substitute container runtime is installed or started
on the Windows development machine. W3 uses ordinary local processes. The
`pwsh` path must come from a trusted local installation; no installer is invoked.

## Exact-head independent gates

- Compile on Windows 2025, Ubuntu 24.04, and macOS 26.
- Start real disposable idle children on hosted free GHA, then stop them.
- Refuse relative executable names or arbitrary commands.
- Refuse duplicate starts and more than two supervised children.
- Keep independent session processes and reject shared-workspace collisions.
- Confirm stop before forgetting process ownership.
- Verify the GUI's keyboard-reachable start/stop/status automation identifiers.
- Review the lack of stdin write methods and provider messaging calls.
- Reconcile actual Windows native HIL only when specifically admitted.

## Next work before interactive terminal acceptance

1. Independent peer review of executable provenance and inherited environment.
2. OS-native child process groups (Windows Job Objects, Linux cgroup/rlimits,
   macOS resource ceilings) with hard enforced RSS/CPU/pids and reaping guarantees.
3. A separate PTY/ConPTY design and native Windows/Linux/macOS comparative RDTE.
4. Explicit operator-approved command input with preview, confirmation,
   identity, audit and per-session idempotency semantics.
5. Accessibility UX for live terminal output and screen reader text interaction,
   with bounded ring buffer and secret redaction.
6. Crash, startup failure, process-tree escape, timeout, and resource-pressure
   fault-injection receipts before claiming trustworthy remote execution.

Public CI runs only on public source. TrueNAS private heavy and authenticated
ChatGPT multi-tab HIL have their **own** admission/authorization gates.
