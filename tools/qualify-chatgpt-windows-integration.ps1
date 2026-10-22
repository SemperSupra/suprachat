#Requires -Version 7.0
<#
Offline Windows-native integration batch. NO Docker/Podman/WSL daemon,
NO authenticated or guest ChatGPT sends, NO browser profile access.
The private repo is a fresh, temporary, exact-SHA checkout on this host only.
Only summary.json and native-browser metadata receipts should be shared.
#>
[CmdletBinding()]
param(
    [string]$WorkspaceRoot = (Join-Path $HOME 'Projects/SemperSupra/SupraChat'),
    [string]$ReceiptRoot = (Join-Path $HOME 'SupraChatQualification'),
    [switch]$SkipNativeBrowsers,
    [switch]$NoBootstrap
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$privateBranch = 'fix/49-fair-target-queue-cow-20261021'
$expectedPrivateHead = 'a97b5131bf7d121a92ecbd62fc75f21c4035972c'
$expectedPublicBranch = 'feat/19-readonly-relay-readiness-cow-20261021'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$id = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$evidence = Join-Path ([IO.Path]::GetFullPath($ReceiptRoot)) $id
$privateRoot = Join-Path $env:TEMP ('suprachat-private-owned-' + [guid]::NewGuid().ToString('N'))
$script:Stages = [System.Collections.Generic.List[object]]::new()
$script:PublicHead = $null
$script:PrivateHead = $null
$script:PrivateCreated = $false
$script:Cleanup = 'NOT_NEEDED'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Require-Exit([string]$Reason) {
    if ($LASTEXITCODE -ne 0) { throw $Reason }
}
function Stage([string]$Name, [scriptblock]$Action) {
    $global:LASTEXITCODE = 0
    try {
        & $Action | Out-Host
        Require-Exit 'COMMAND_NONZERO'
        $script:Stages.Add([pscustomobject]@{name=$Name;state='PASS'})
    } catch {
        $reason = if ($_.Exception.Message -match '^[A-Z][A-Z0-9_]+$') { $_.Exception.Message } else { $_.Exception.GetType().Name }
        $script:Stages.Add([pscustomobject]@{name=$Name;state='FAIL';reason_code=$reason})
        throw
    }
}
function Has-Sdk10 {
    $binary = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $binary) { return $false }
    $versions = @(& $binary.Source --list-sdks 2>$null)
    return ($LASTEXITCODE -eq 0 -and [bool](@($versions | Where-Object { $_ -match '^10[.]0[.][0-9]+\s' }).Count))
}
function Has-Node22 {
    $binary = Get-Command node -ErrorAction SilentlyContinue
    if (-not $binary) { return $false }
    $version = (& $binary.Source --version 2>$null | Out-String).Trim()
    return ($LASTEXITCODE -eq 0 -and $version -match '^v(2[2-9]|[3-9][0-9])[.]')
}
try {
    Stage 'read-only-source-and-host-inventory' {
        if (-not $IsWindows) { throw 'WINDOWS_REQUIRED' }
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'GIT_REQUIRED' }
        if (-not (Test-Path -LiteralPath $WorkspaceRoot -PathType Container)) { throw 'WORKSPACE_MISSING' }
        $branch = (& git -C $root branch --show-current | Out-String).Trim()
        Require-Exit 'PUBLIC_BRANCH_READ_FAILED'
        if ($branch -ne $expectedPublicBranch) { throw 'PUBLIC_BRANCH_MISMATCH' }
        $script:PublicHead = (& git -C $root rev-parse HEAD | Out-String).Trim()
        Require-Exit 'PUBLIC_SOURCE_SHA_FAILED'
    }
    Stage 'reuse-or-install-project-local-toolchain' {
        $needDotnet = -not (Has-Sdk10)
        $needNode = -not (Has-Node22)
        if ($NoBootstrap -and ($needDotnet -or $needNode)) { throw 'BOOTSTRAP_DISABLED' }
        $bootstrap = Join-Path $PSScriptRoot 'bootstrap-chatgpt-windows-local.ps1'
        $null = . $bootstrap -ProjectRoot $root -NeedDotnet:$needDotnet -NeedNode:$needNode
        if (-not (Has-Sdk10)) { throw 'DOTNET10_UNAVAILABLE' }
        if (-not (Has-Node22)) { throw 'NODE22_UNAVAILABLE' }
        $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $root '.local/suprachat-qualification/playwright-browsers'
    }
    Stage 'isolated-private-source-exact-head' {
        $script:PrivateCreated = $true
        & git clone --quiet --depth 1 --branch $privateBranch 'https://github.com/SupraShellScripts/userscripts-private.git' $privateRoot
        Require-Exit 'PRIVATE_CLONE_FAILED'
        $script:PrivateHead = (& git -C $privateRoot rev-parse HEAD | Out-String).Trim()
        Require-Exit 'PRIVATE_SOURCE_SHA_FAILED'
        if ($script:PrivateHead -ne $expectedPrivateHead) { throw 'PRIVATE_SHA_MISMATCH' }
    }
    Stage 'public-cli-release-build' {
        & dotnet build (Join-Path $root 'SupraChat.Automation/SupraChat.Automation.csproj') -c Release --nologo
        Require-Exit 'PUBLIC_BUILD_FAILED'
    }
    Stage 'private-unit-and-security-batch' {
        Push-Location $privateRoot
        try {
            & node --test 'tests/unit/chatgpt-local-bridge.test.mjs'
            Require-Exit 'PRIVATE_UNIT_FAILED'
            & node 'tools/build.mjs' --clean --script chatgpt-local-bridge
            Require-Exit 'PRIVATE_BUILD_FAILED'
            & node 'tools/validate.mjs' --script chatgpt-local-bridge
            Require-Exit 'PRIVATE_VALIDATE_FAILED'
            & node 'tools/security-scan.mjs'
            Require-Exit 'PRIVATE_SECURITY_FAILED'
        } finally { Pop-Location }
    }
    if (-not $SkipNativeBrowsers) {
        Stage 'native-chromium-firefox-webkit' {
            $owned = Join-Path $root '.local/suprachat-qualification'
            $pkgRoot = Join-Path $owned 'native-playwright'
            $pkgFile = Join-Path $pkgRoot 'node_modules/playwright/package.json'
            $valid = (Test-Path -LiteralPath $pkgFile) -and ((Get-Content -Raw -LiteralPath $pkgFile | ConvertFrom-Json).version -eq '1.62.0')
            if (-not $valid) {
                $bin = (Get-Command node -ErrorAction Stop).Source
                $npm = Join-Path (Split-Path -Parent $bin) 'node_modules/npm/bin/npm-cli.js'
                if (-not (Test-Path -LiteralPath $npm)) { throw 'NPM_NOT_FOUND' }
                New-Item -ItemType Directory -Path $pkgRoot -Force | Out-Null
                & node $npm install --prefix $pkgRoot --ignore-scripts --no-audit --no-fund --save-exact 'playwright@1.62.0'
                Require-Exit 'PLAYWRIGHT_INSTALL_FAILED'
            }
            $cli = Join-Path $pkgRoot 'node_modules/playwright/cli.js'
            & node $cli install chromium firefox webkit
            Require-Exit 'PLAYWRIGHT_BROWSERS_FAILED'
            $nativeReceipt = Join-Path $evidence 'native-bridge-browser-smoke.json'
            & node (Join-Path $PSScriptRoot 'run-bridge-native-browser-smoke.mjs') --private-root $privateRoot --playwright-root $pkgRoot --receipt $nativeReceipt --browsers chromium,firefox,webkit
            Require-Exit 'NATIVE_BROWSER_SMOKE_FAILED'
        }
    } else {
        $script:Stages.Add([pscustomobject]@{name='native-chromium-firefox-webkit';state='SKIP';reason_code='OPERATOR_SKIP'})
    }
    Stage 'real-private-relay-readonly-cli-jsonrpc-parity' {
        & node (Join-Path $PSScriptRoot 'qualify-chatgpt-loopback.mjs') --mode private --public-root $root --private-root $privateRoot --private-sha $expectedPrivateHead
        Require-Exit 'LOOPBACK_PARITY_FAILED'
    }
} catch {
    # No automatic retry after failure. The first failing gate remains recorded.
} finally {
    # The sole owned delete target is a newly chosen private COW under TEMP.
    $tempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    $leaf = Split-Path -Leaf $privateRoot
    if ($script:PrivateCreated -and $privateRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        $leaf -match '^suprachat-private-owned-[0-9a-f]{32}$') {
        try {
            if (Test-Path -LiteralPath $privateRoot) { Remove-Item -LiteralPath $privateRoot -Recurse -Force -ErrorAction Stop }
            $script:Cleanup = 'OWNED_PRIVATE_WORKTREE_REMOVED'
        } catch { $script:Cleanup = 'CLEANUP_FAILED' }
    }
    $summary = [ordered]@{
        schema = 'suprachat-large-native-integration-batch/v1'
        utc = (Get-Date).ToUniversalTime().ToString('o')
        public_head = $script:PublicHead
        private_head = $script:PrivateHead
        expected_private_head = $expectedPrivateHead
        native_browser_enabled = -not $SkipNativeBrowsers
        visited_real_chatgpt = $false
        sent_prompt = $false
        used_docker = $false
        exported_private_source = $false
        credential_material_in_receipt = $false
        private_checkout_cleanup = $script:Cleanup
        stages = @($script:Stages)
    }
    $output = Join-Path $evidence 'summary.json'
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8
    Write-Host "Metadata-only summary: $output"
    Get-Content -Raw -LiteralPath $output | Write-Host
    if (@($script:Stages | Where-Object { $_.state -eq 'FAIL' }).Count -gt 0 -or $script:Cleanup -eq 'CLEANUP_FAILED') { exit 1 }
}
