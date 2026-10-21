# SupraChat #19 — Windows local actor batch (2026-10-20)

This qualification batch does not change product scope and does not authorize
an unattended agent, account use, credential transfer or GitHub write.

## Requirements

Windows 10/11, PowerShell 7, Git and .NET SDK 10. The optional private
browser/bridge checks require Node.js 22+ and GitHub CLI with existing
read access to SupraShellScripts/userscripts-private. Docker Desktop or another
supported Docker engine enables the existing private heavy browser gate.

## One batch (PowerShell 7)

    $dir = Join-Path $env:TEMP ("suprachat-qual-" + [guid]::NewGuid().ToString("N"))
    git clone --depth 1 --branch feat/19-chatgpt-control-contract-20261020 https://github.com/SemperSupra/suprachat.git $dir
    if ($LASTEXITCODE -ne 0) { throw "SupraChat clone failed" }
    Set-Location $dir
    pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\qualify-chatgpt-windows-local.ps1 -IncludePrivateBridge -ContainerHeavy

On Windows systems with Restricted execution policy, the process-local -ExecutionPolicy Bypass option avoids changing machine/user policy. It must be invoked with pwsh (PowerShell 7), not Windows PowerShell 5.1. If Group Policy enforces AllSigned or another restrictive policy, do not circumvent organizational controls; obtain the permitted execution path instead. Verify PowerShell 7 with Get-Command pwsh and inspect effective policies using Get-ExecutionPolicy -List.

Remove -IncludePrivateBridge -ContainerHeavy for a public-only run. Add
-SkipLiveGuest to prohibit even the one anonymous harmless prompt.

## Scope

1. Compile standalone .NET browser probe and install Chromium.
2. Run six offline synthetic browser scenarios, with assertions.
3. In a new isolated Chromium context without profile/cookies/login, navigate
   to https://chatgpt.com and, only when a recognized guest send control
   exists, attempt ONE harmless message: Please reply with exactly
   GUEST_PROBE_OK and nothing else.
4. If opted into private repo access, clone EXACT private PR #104 head
   577ec904abe36ab58e0980bf1bbcacb24c7db701 and run Node tests,
   build/validation/security and optional container heavy qualification.
   Fail closed if the private PR's branch head has changed.
5. Leave only metadata-only receipts and private checkout on disk:
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
