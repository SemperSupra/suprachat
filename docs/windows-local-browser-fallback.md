# SupraChat #19 — Windows actor bridge-browser recovery (2026-10-21)

## Authoritative local actor receipt

The operator provided an Oct 21 local Windows run of public head
`fe528d592001afcc0083d749dd856ca5a23292ec` and private userscripts
PR #104 head `577ec904abe36ab58e0980bf1bbcacb24c7db701`.
Discovery: reusable existing-workspace .NET SDK 10.0.401, project-local
official-checksum-verified Node v24.21.0; .NET guest probe built and
Chromium installed; guest taxonomy + all six synthetic cases PASSED.
Real ChatGPT guest call SKIPPED by default. Private Node tests 23/23 PASS,
single-script build/validate/security PASS (security scan: 176 files).
Only blocked stage was canonical Docker/Podman/nerdctl/WSL container-heavy
qualification; tools/ci/run-gates.sh is container-toolchain dependent.
The actor checked for executable commands but no usable container daemon
was detected by the original ci.ps1. No request was sent to ChatGPT.

## Bounded corrective path

**Current Windows host policy supersedes the earlier runtime-probe plan:**
Docker Engine/Desktop and any replacement container daemon are prohibited on
the development box. The Windows actor never probes, installs, enables,
starts or switches container engines. The legacy `-ContainerHeavy` switch is a
no-op that emits `private-bridge-heavy-browser-ci=SKIP` with reason
`WINDOWS_CONTAINER_HOST_PROHIBITED`. The independent native Playwright lane
runs only with explicit `-NativeBrowserSmoke`; no Docker dependency.

Native smoke uses version-pinned Playwright 1.62.0, installed only inside
the disposable checkout's ownership-marked `.local/suprachat-qualification`
namespace. It downloads browser engines into that same owned namespace
and runs the existing **private** `tests/browser/smoke.mjs` against the
private userscript at a synthetic, intercepted ChatGPT URL. Chromium,
Firefox and WebKit are attempted with isolated browser contexts.
Private source stays in the local checkout and is never copied to public
GitHub Actions. No authenticated browser profile, cookie, credential or
real ChatGPT prompt is required. The synthetic smoke result cannot be
represented as a PASS for the full canonical container-heavy suite.

## New pinned candidate and remaining gates

The next local actor batch is pinned to private userscripts stacked draft
PR #105 at commit `5adceaae29266465fa5c49ff72ab865876c3129b`,
which follows #104 and implements fail-closed uncertainty quarantine.
Private minimal CI already passed 26/26 tests at this exact head. Full
three-browser native smoke on the Windows actor remains unobserved until
the actor runs the updated script. Separately, canonical container-heavy
lint/repro/browser and real authenticated HIL remain distinct gates.

From the previously created disposable checkout, use Git -C with the
absolute checkout path; the `C:\Users\Mark\Projects\SemperSupra\SupraChat`
umbrella workspace is read-only discovery input, not a Git checkout.

    $checkout = 'C:\Users\Mark\AppData\Local\Temp\suprachat-qual-2a135668507f48ca93fa2529acba9c53'
    $workspace = 'C:\Users\Mark\Projects\SemperSupra\SupraChat'
    git -C $checkout pull --ff-only origin feat/19-chatgpt-control-contract-20261020
    if ($LASTEXITCODE -ne 0) { throw 'Public branch update failed' }
    $script = Join-Path $checkout 'tools\qualify-chatgpt-windows-local.ps1'
    pwsh -NoProfile -ExecutionPolicy Bypass -File $script -WorkspaceRoot $workspace -IncludePrivateBridge -NativeBrowserSmoke

The default does not issue a real ChatGPT prompt. The exact private
commit is validated on checkout; a mismatch aborts safely. Only the
new `summary.json` and metadata-only `native-bridge-browser-smoke.json`
should be shared after review; do not upload private code or profiles.

## Authority

No private-source publication, spend, merge, authenticated-chat action,
credential delegation or remote-host installation is authorized by this
packet. A working TrueNAS/container executor can qualify the canonical
heavy gate separately if such access becomes available. Browser-live
two-tab, interrupted-ack, reauth, exact-turn correlation and post-click
failure scenarios are unproven on the real provider.
