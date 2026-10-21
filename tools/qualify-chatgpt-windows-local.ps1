#Requires -Version 7.0
<#
One-shot Windows dev-box qualification for SupraChat #19.
No authenticated ChatGPT messages, cookies, browser profiles, credential
exports, GitHub writes, private-source publication or automatic merges.
The anonymous probe may submit ONE benign guest prompt if guest access works.
Only summary.json should be shared with the project.
#>
[CmdletBinding()]
param(
    [switch]$IncludePrivateBridge,
    [switch]$ContainerHeavy,
    [switch]$SkipLiveGuest,
    [switch]$NoBootstrap,
    [switch]$BootstrapOnly,
    [switch]$ForcePortableTools,
    [string]$ReceiptRoot
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PrivateExpectedHead = '577ec904abe36ab58e0980bf1bbcacb24c7db701'
$PrivateBranch = 'fix/49-bound-conversation-cow-20261020'
$PublicRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RunId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if (-not $ReceiptRoot) { $ReceiptRoot = Join-Path $HOME 'SupraChatQualification' }
$WorkRoot = Join-Path ([IO.Path]::GetFullPath($ReceiptRoot)) $RunId
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
$script:Stages = [System.Collections.Generic.List[object]]::new()
$script:SourcePrivate = $null
$script:Tooling = [ordered]@{ dotnet_before = $null; node_before = $null; dotnet_after = $null; node_after = $null; bootstrap = $null; scope = 'project-local-or-existing' }

function Add-Stage([string]$Name, [string]$State, [string]$Reason = '') {
    $script:Stages.Add([ordered]@{
        stage = $Name
        state = $State
        reason_code = $Reason
    })
    Write-Host "[$State] $Name $Reason"
}

function Run-Stage([string]$Name, [scriptblock]$Action) {
    Write-Host "Starting $Name"
    $global:LASTEXITCODE = 0
    try {
        & $Action | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "COMMAND_EXIT_$LASTEXITCODE" }
        Add-Stage $Name 'PASS'
        return $true
    } catch {
        $reason = if ($_.Exception.Message -match '^(?:COMMAND_EXIT_\d+|(?:BOOTSTRAP|SDK|NODE|DOTNET|PRIVATE|SOURCE|DOWNLOAD|HASH|ARCH|TOOL)_[A-Z0-9_]+)$') {
            $_.Exception.Message
        } else {
            $_.Exception.GetType().Name
        }
        Add-Stage $Name 'FAIL' $reason
        return $false
    }
}

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "REQUIRED_TOOL_MISSING_$Name"
    }
}

function Assert-Head([string]$Root, [string]$Expected) {
    $sha = (& git -C $Root rev-parse HEAD 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sha -ne $Expected) {
        throw 'SOURCE_HEAD_MISMATCH'
    }
}

function Invoke-Guest([string[]]$Arguments, [string]$File) {
    & dotnet run --project (Join-Path $PublicRepoRoot 'SupraChat.GuestProbe/SupraChat.GuestProbe.csproj') -c Release --no-build -- @Arguments --output $File | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "GUEST_PROBE_EXIT_$LASTEXITCODE" }
    if (-not (Test-Path $File)) { throw 'RECEIPT_MISSING' }
    return Get-Content -Raw $File | ConvertFrom-Json
}

try {
    $hasDotnet = Run-Stage 'dotnet-preflight' {
        Require-Command 'dotnet'
        & dotnet --version
    }

    if ($hasDotnet) {
        $built = Run-Stage 'guest-probe-build' {
            & dotnet build (Join-Path $PublicRepoRoot 'SupraChat.GuestProbe/SupraChat.GuestProbe.csproj') -c Release
        }

        if ($built) {
            $installed = Run-Stage 'playwright-chromium-install' {
                $driver = Join-Path $PublicRepoRoot 'SupraChat.GuestProbe/bin/Release/net10.0/playwright.ps1'
                if (-not (Test-Path $driver)) { throw 'PLAYWRIGHT_DRIVER_MISSING' }
                & $driver install chromium
            }

            if ($installed) {
                Run-Stage 'guest-taxonomy-selftest' {
                    & dotnet run --project (Join-Path $PublicRepoRoot 'SupraChat.GuestProbe/SupraChat.GuestProbe.csproj') -c Release --no-build -- --self-test
                } | Out-Null

                $expected = [ordered]@{
                    'success'         = 'GUEST_TURN_CORRELATED'
                    'wrong-turn'      = 'ASSISTANT_TURN_OBSERVED_MARKER_MISSING'
                    'no-composer'     = 'COMPOSER_NOT_FOUND'
                    'no-send-control' = 'SEND_CONTROL_UNAVAILABLE'
                    'login-gate'      = 'LOGIN_GATE'
                    'challenge'       = 'CHALLENGE_OR_BROWSER_CHECK'
                }
                foreach ($case in $expected.Keys) {
                    $caseName = $case
                    Run-Stage "fixture-$caseName" {
                        $dest = Join-Path $WorkRoot "fixture-$caseName.json"
                        $receipt = Invoke-Guest @('--fixture-case', $caseName) $dest
                        if ($receipt.final_state -ne $expected[$caseName] -or
                            $receipt.source -ne 'synthetic-local-route' -or
                            $receipt.auth_supplied -or -not $receipt.page_reached) {
                            throw 'FIXTURE_ACCEPTANCE_MISMATCH'
                        }
                    } | Out-Null
                }

                if ($SkipLiveGuest) {
                    Add-Stage 'real-anonymous-guest' 'SKIP' 'OPERATOR_SKIPPED'
                } else {
                    Run-Stage 'real-anonymous-guest' {
                        $receipt = Invoke-Guest @() (Join-Path $WorkRoot 'guest-receipt.json')
                        if ($receipt.final_state -eq 'HARNESS_FAILURE') {
                            throw 'GUEST_HARNESS_FAILED'
                        }
                    } | Out-Null
                }
            }
        }
    }

    if ($IncludePrivateBridge) {
        $ready = Run-Stage 'private-bridge-tool-preflight' {
            Require-Command 'gh'
            Require-Command 'git'
            Require-Command 'node'
            $raw = (& node -p 'process.versions.node' | Out-String).Trim()
            if ($LASTEXITCODE -ne 0 -or [int]($raw.Split('.')[0]) -lt 22) {
                throw 'NODE22_REQUIRED'
            }
            & gh auth status *> $null
            if ($LASTEXITCODE -ne 0) { throw 'GH_AUTH_REQUIRED' }
        }
        if ($ready) {
            $privateRoot = Join-Path $WorkRoot 'private-bridge-checkout'
            $cloned = Run-Stage 'pinned-private-bridge-checkout' {
                & gh repo clone 'SupraShellScripts/userscripts-private' $privateRoot -- --branch $PrivateBranch --depth 1
                if ($LASTEXITCODE -ne 0) { throw 'PRIVATE_CLONE_FAILED' }
                Assert-Head $privateRoot $PrivateExpectedHead
                $script:SourcePrivate = $PrivateExpectedHead
            }
            if ($cloned) {
                Push-Location $privateRoot
                try {
                    Run-Stage 'private-bridge-full-unit-suite' {
                        $tests = @(Get-ChildItem (Join-Path $privateRoot 'tests/unit') -Filter '*.test.mjs' | Sort-Object Name | ForEach-Object FullName)
                        if ($tests.Count -eq 0) { throw 'NO_UNIT_TESTS' }
                        & node --test @tests
                    } | Out-Null
                    Run-Stage 'private-bridge-build' {
                        & node tools/build.mjs --clean --script chatgpt-local-bridge
                    } | Out-Null
                    Run-Stage 'private-bridge-validation' {
                        & node tools/validate.mjs --script chatgpt-local-bridge
                    } | Out-Null
                    Run-Stage 'private-bridge-security' {
                        & node tools/security-scan.mjs
                    } | Out-Null
                    if ($ContainerHeavy) {
                        if (Get-Command docker -ErrorAction SilentlyContinue) {
                            Run-Stage 'private-bridge-heavy-browser-ci' {
                                & (Join-Path $privateRoot 'ci.ps1') heavy -Script chatgpt-local-bridge
                            } | Out-Null
                        } else {
                            Add-Stage 'private-bridge-heavy-browser-ci' 'SKIP' 'DOCKER_NOT_AVAILABLE'
                        }
                    }
                } finally { Pop-Location }
            }
        }
    }
}
finally {
    $publicHead = 'unavailable'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $publicHead = (& git -C $PublicRepoRoot rev-parse HEAD 2>$null | Out-String).Trim()
    }
    $receipt = [ordered]@{
        schema = 'suprachat-local-windows-batch/v1'
        run_utc = (Get-Date).ToUniversalTime().ToString('o')
        source_public_head = $publicHead
        source_private_head = $script:SourcePrivate
        source_private_expected = if ($IncludePrivateBridge) { $PrivateExpectedHead } else { $null }
        includes_private_source = $false
        has_credentials = $false
        has_chatgpt_transcript = $false
        has_browser_cookie_material = $false
        guest_probe_enabled = -not $SkipLiveGuest
        stages = @($script:Stages)
    }
    $summary = Join-Path $WorkRoot 'summary.json'
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summary -Encoding utf8
    Write-Host ""
    Write-Host 'LOCAL QUALIFICATION COMPLETE.'
    Write-Host "Safe to share: $summary"
    Write-Host 'Do not share the private checkout or arbitrary browser files.'
    Get-Content -Raw $summary | Write-Host
}