# SupraChat #19 — container execution alternatives and host admission (2026-10-21)

## Canonical heavy gate

Private userscripts source authority is `SupraShellScripts/userscripts-private` PR #105.
Canonical `ci.ps1 heavy -Script chatgpt-local-bridge` builds `.ci/images/toolchain/Dockerfile`
using `mcr.microsoft.com/playwright:v1.62.0-noble`: a Linux Ubuntu 24.04
OCI image. The pinned image installs the following build and assurance tools:
Node/Playwright 1.62.0, browsers Chromium/Firefox/WebKit, eslint, actionlint,
shellcheck, hadolint, PowerShell 7.6.2/PSScriptAnalyzer; it runs build, lint,
policy/security, reproducibility, version and browser fixture gates.
The host requires a usable **Linux Docker-compatible OCI engine**, x86_64 CPU,
adequate disk/RAM, and a disposable working copy of the private repository.
It does not require a Windows container, a GUI, a GPU or nested hypervisor.

## Environment decision matrix

| Lane | Canonical Linux image | Private source permitted | Admission |
| --- | --- | --- | --- |
| TrueNAS SCALE Apps Docker | Native Linux OCI yes | Yes within authorized local custody | Capacity-one Apps runner and host headroom must be freshly checked |
| Public Ubuntu 24.04 GHA | Native Linux Docker yes | No; public-safe source only | Public fixtures/projection, independent evidence |
| Public Windows 2025 GHA | No; Windows daemon is a separate image OS | No; public-safe only | Qualify native Windows-container feasibility separately |
| Public macOS GHA | No native Linux daemon | Public-safe only | Native Playwright and CLI, no canonical Linux container |
| Windows dev box in Linux container mode | Yes through WSL2 or Hyper-V Linux VM | Yes, local authorized private clone | Docker/Podman runtime must be usable, process/host admission first |
| Windows dev box in Windows container mode | No; Linux image rejected | Yes locally | Separate Windows OCI testing needs separate NanoServer/ServerCore image |

## Public free-tier evidence

Public SupraChat draft PR #25 adds `.github/workflows/verify-container-surfaces.yml`.
Run `39549815870` (public hosted) PASS: Ubuntu24.04 Docker Linux image build
and three independent Playwright engines in network-disabled Linux container;
Windows2025 exposes reachable Windows Docker daemon, Windows Containers
feature enabled, no WSL distro. This is **not** the private canonical heavy gate.
A subsequent run adds a real Windows Nano Server `ltsc2025` process container
smoke to distinguish daemon presence from functioning Windows containers.

## Existing TrueNAS GARM authority

`SemperSupra/garm-provider-truenas-private#43` records a real capacity-one
Apps runner as an accepted operational baseline, distinct from unadmitted
TrueNAS-managed LXC/System Container or VM backends. `SemperSupra/agent-dispatch-private#480`
owns heavier TrueNAS admission/headroom rules. Do not infer that this
project may launch another job just because Docker Apps are available.
A one-off private source test may run only after fresh capacity/credential
and execution admission. Avoid altering Apps config, nested VM state, daemon
network sockets or NAS service workloads.

## Windows development-box census (read only)

Run the committed public inspector, not arbitrary installer commands:

    $checkout = 'C:\Users\Mark\AppData\Local\Temp\suprachat-qual-2a135668507f48ca93fa2529acba9c53'
    $workspace = 'C:\Users\Mark\Projects\SemperSupra\SupraChat'
    git -C $checkout pull --ff-only origin feat/19-chatgpt-control-contract-20261020
    if ($LASTEXITCODE -ne 0) { throw 'Checkout update failed' }
    $script = Join-Path $checkout 'tools\inspect-windows-container-host.ps1'
    pwsh -NoProfile -ExecutionPolicy Bypass -File $script

The inspector reports only OS/architecture/build, Hyper-V/Containers/WSL
feature status (where readable), firmware virtualization, Docker daemon
reachability and OS type; it does not enable features, run containers,
change boot configuration, install privileged services, or capture secrets.
Windows 10/11 Pro/Enterprise plus required virtualization features are
normally needed for Windows containers. Docker Desktop **all-users**
installation is required for Windows-container mode; user-scoped Docker
Desktop installs only support Linux containers. Windows Server containers
have version/edition and Hyper-V isolation constraints; do not infer
capability from a `docker.exe` command alone.

After inspecting the user's actual host, choose one of: (a) use existing
TrueNAS/Linux OCI for canonical gates; (b) optionally provision isolated
Linux containers on Windows via WSL2; (c) investigate a separate Windows
NanoServer/ServerCore testing lane, which cannot run the current Linux image.
Never automatically enable machine-wide virtualization/features, install
services, reboot the host or switch the user's Docker daemon mode.

## Gate and evidence semantics

Public Linux synthetic browser success demonstrates container feasibility;
private exact-head canonical suite is still NOT_EXECUTED unless a private
admitted executor actually runs it. Windows container capability on a
public Windows GHA runner cannot establish capability on the user's
separate Windows dev box. No private source or credentials are uploaded
to public Actions. Cross-host filesystem mounts must not be assumed:
remote Docker daemons cannot directly bind-mount a Windows client's
local checkout path. Use an admitted remote private checkout or a
reversible, custody-reviewed artifact transfer for TrueNAS.
