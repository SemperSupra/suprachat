# SupraChat #19 — Windows local actor batch (2026-10-20)

This qualification batch does not change product scope and does not authorize
an unattended agent, account use, credential transfer or GitHub write.

## Requirements

Windows 10/11 (x64), PowerShell 7 and Git. The script first observes the tool inventory. If the .NET 10 SDK or Node.js 22+ is missing, it downloads the exact pinned portable .NET SDK 10.0.402 and Node.js v24.21.0 ZIPs, verifies SHA-512/SHA-256 against Microsoft's and Node.js's official release metadata, and expands them under the disposable checkout's .local directory. It uses process-local PATH and project-local NuGet/Playwright caches only. No administrator access, winget or system-wide installer is needed. Existing valid SDK/Node versions are reused. GitHub CLI is optional; the private repository uses the user's existing Git credentials. Docker Engine/Desktop and other container daemons are explicitly prohibited on the Windows development box. Canonical Linux OCI heavy qualification belongs on an admitted TrueNAS Linux executor.

## Important: workspace root versus executable checkout

The parent folder C:\Users\Mark\Projects\SemperSupra\SupraChat is an umbrella
workspace and is NOT itself a Git repository. There is no .git directory and
no tools/qualify-chatgpt-windows-local.ps1 at that parent level. The public
repo may exist at the child folder suprachat, but never assume it is the
qualification branch or modify it in place. Reuse the previously cloned
disposable checkout at:

    C:\Users\Mark\AppData\Local\Temp\suprachat-qual-2a135668507f48ca93fa2529acba9c53

Run Git with -C against the actual checkout and pass the script's absolute
path to pwsh -File. Pass the established umbrella workspace separately using
-WorkspaceRoot. A tested self-contained checkout-selection snippet is
maintained in docs/windows-local-repeatability-contract.md.

## One batch (PowerShell 7)

    $dir = Join-Path $env:TEMP ("suprachat-qual-" + [guid]::NewGuid().ToString("N"))
    git clone --depth 1 --branch feat/19-chatgpt-control-contract-20261020 https://github.com/SemperSupra/suprachat.git $dir
    if ($LASTEXITCODE -ne 0) { throw "SupraChat clone failed" }
    Set-Location $dir
    pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\qualify-chatgpt-windows-local.ps1 -IncludePrivateBridge -NativeBrowserSmoke

On Windows systems with Restricted execution policy, the process-local -ExecutionPolicy Bypass option avoids changing machine/user policy. It must be invoked with pwsh (PowerShell 7), not Windows PowerShell 5.1. If Group Policy enforces AllSigned or another restrictive policy, do not circumvent organizational controls; obtain the permitted execution path instead. Verify PowerShell 7 with Get-Command pwsh and inspect effective policies using Get-ExecutionPolicy -List.

Remove -IncludePrivateBridge -NativeBrowserSmoke for a public-only run. Add -SkipLiveGuest to prohibit even one anonymous benign prompt. Add -BootstrapOnly for a pure tooling-discovery/installation/verification pass; add -NoBootstrap to fail instead of downloading; add -ForcePortableTools to independently prove the installer path even when system tooling exists. Use a process-scoped PowerShell 7 -ExecutionPolicy Bypass only when locally permitted. If an organizational Group Policy forbids running scripts, use an approved execution mechanism rather than circumvent it.

## Scope

1. Observe the actual Git/SDK/Node inventory, plan only missing tools, install verified portable ZIPs into .local, and verify versions from the resulting process environment. The exact versions and checksum evidence are preserved in the local receipt.
2. Compile standalone .NET browser probe and install Chromium.
3. Run six offline synthetic browser scenarios, with assertions.
4. In a new isolated Chromium context without profile/cookies/login, navigate
   to https://chatgpt.com and, only when a recognized guest send control
   exists, attempt ONE harmless message: Please reply with exactly
   GUEST_PROBE_OK and nothing else.
5. If opted into private repo access, clone EXACT private PR #104 head
   577ec904abe36ab58e0980bf1bbcacb24c7db701 and run Node tests,
   build/validation/security and optional native Playwright fixture qualification. No Windows container engine is probed or started.
   Fail closed if the private PR's branch head has changed.
6. Leave only metadata-only receipts and private checkout on disk:
   $HOME\SupraChatQualification\<UTC-run-id>\summary.json

The script neither controls an already signed-in ChatGPT conversation nor
installs Violentmonkey. It never reads local Chrome/Edge/Firefox profile data,
extracts credentials, executes model-provided shell commands, publishes
private source, uploads conversation text or pushes GitHub changes.

## Evidence and pending gates

Share ONLY summary.json. Browser case receipts are synthetic, or contain
only classifications/DOM counts/HTTP status. Do not share the adjacent
private-bridge-checkout directory.

Passing synthetic test cases cannot establish real-site access. Existing
private userscripts PR #104 has a recorded ambiguous-delivery client
quarantine issue. It must be fixed and independently verified before
separately authorizing any signed-in, real-conversation submission. All
local/authenticated HIL, independent validation, merges and releases
remain separately gated. The local script exits when the batch is complete;
it does not create a background worker.

## Recovery after the first 2026-10-21 actor receipt

The first Windows actor successfully cloned the public recovery branch but detected `No .NET SDKs were found`. The private preflight returned a generic failure because it unnecessarily required `gh` and did not self-provision Node. This is **TOOLING_NOT_PRESENT**, not a ChatGPT/browser test regression. Update the existing disposable checkout with `git pull --ff-only` (do not reclone), then invoke the script with the same PowerShell 7 process-scoped entrypoint. The script now performs Discover -> Plan -> Apply -> Verify and records actionable stages.

The .local directory is excluded only in the checkout's local Git exclude; removing the disposable checkout removes the tool installation. Large SDK and browser downloads require network access to official Microsoft, Node.js, NuGet and Playwright hosts. If blocked, the receipt records a dependency/bootstrap blocker rather than using unofficial mirrors, bypassing proxies, or installing globally.
