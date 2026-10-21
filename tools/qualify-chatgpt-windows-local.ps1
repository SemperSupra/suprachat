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
    [switch]$RunLiveGuest,
    [switch]$DiscoverOnly,
    [string]$WorkspaceRoot = (Join-Path $HOME 'Projects/SemperSupra/SupraChat'),
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
$script:Tooling = [ordered]@{
    dotnet_before = $null
    node_before = $null
    dotnet_after = $null
    node_after = $null
    dotnet_origin = 'not-qualified'
    node_origin = 'not-qualified'
    workspace_present = $null
    workspace_dirs_searched = 0
    workspace_sdk10 = $null
    workspace_node22 = $null
    bootstrap = $null
    scope = 'project-local-or-existing'
}

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


function Discover-DotnetSdk10 {
    $binary = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $binary) { return $null }
    $inventory = @(& $binary.Source --list-sdks 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }
    $matched = $inventory | Where-Object { $_ -match '^10[.]0[.][0-9]+\s' } | Select-Object -First 1
    if ($matched) { return ($matched -split '\s+')[0] }
    return $null
}
function Discover-Node22 {
    $binary = Get-Command node -ErrorAction SilentlyContinue
    if (-not $binary) { return $null }
    $version = (& $binary.Source --version 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^v([0-9]+)[.]([0-9]+)[.]([0-9]+)$') { return $null }
    if ([int]$Matches[1] -lt 22) { return $null }
    return $version
}

function Invoke-Guest([string[]]$Arguments, [string]$File) {
    & dotnet run --project (Join-Path $PublicRepoRoot 'SupraChat.GuestProbe/SupraChat.GuestProbe.csproj') -c Release --no-build -- @Arguments --output $File | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "GUEST_PROBE_EXIT_$LASTEXITCODE" }
    if (-not (Test-Path $File)) { throw 'RECEIPT_MISSING' }
    return Get-Content -Raw $File | ConvertFrom-Json
}

try {
    $observed = Run-Stage 'discover-host-tooling' {
        Require-Command 'git'
        $script:Tooling.dotnet_before = Discover-DotnetSdk10
        $script:Tooling.node_before = Discover-Node22
        Write-Host "Discovered on PATH: .NET 10 SDK=$($script:Tooling.dotnet_before ?? 'MISSING'); Node >=22=$($script:Tooling.node_before ?? 'MISSING')"
    }
    $workspaceObserved = Run-Stage 'discover-existing-suprachat-workspace' {
        $discoveryScript = Join-Path $PSScriptRoot 'discover-chatgpt-windows-workspace.ps1'
        $snapshot = & $discoveryScript -WorkspaceRoot $WorkspaceRoot
        $script:Tooling.workspace_present = [bool]$snapshot.workspace_present
        $script:Tooling.workspace_dirs_searched = [int]$snapshot.searched_directories
        $script:Tooling.workspace_sdk10 = $snapshot.dotnet_sdk10
        $script:Tooling.workspace_node22 = $snapshot.node22
        if (-not $ForcePortableTools) {
            if (-not $script:Tooling.dotnet_before -and $snapshot.selected_dotnet_path) {
                $dir = Split-Path -Parent $snapshot.selected_dotnet_path
                $env:DOTNET_ROOT = $dir
                $env:PATH = $dir + [IO.Path]::PathSeparator + $env:PATH
                $script:Tooling.dotnet_origin = 'existing-workspace'
            }
            if (-not $script:Tooling.node_before -and $snapshot.selected_node_path) {
                $dir = Split-Path -Parent $snapshot.selected_node_path
                $env:PATH = $dir + [IO.Path]::PathSeparator + $env:PATH
                $script:Tooling.node_origin = 'existing-workspace'
            }
        }
        $script:Tooling.dotnet_before = Discover-DotnetSdk10
        $script:Tooling.node_before = Discover-Node22
        if ($script:Tooling.dotnet_before -and $script:Tooling.dotnet_origin -eq 'not-qualified') {
            $script:Tooling.dotnet_origin = 'existing-path'
        }
        if ($script:Tooling.node_before -and $script:Tooling.node_origin -eq 'not-qualified') {
            $script:Tooling.node_origin = 'existing-path'
        }
        Write-Host "Workspace exists=$($snapshot.workspace_present); bounded_dirs=$($snapshot.searched_directories)"
        Write-Host "Workspace discovery (read-only): SDK10=$($snapshot.dotnet_sdk10 ?? 'NONE'); Node22=$($snapshot.node22 ?? 'NONE')"
    }
    if ($DiscoverOnly) {
        Add-Stage 'tool-plan' 'SKIP' 'DISCOVER_ONLY_NO_INSTALL'
        return
    }
    $needDotnet = [bool]($ForcePortableTools -or -not $script:Tooling.dotnet_before)
    $needNode = [bool]($IncludePrivateBridge -and ($ForcePortableTools -or -not $script:Tooling.node_before))
    if (-not $observed -or -not $workspaceObserved) {
        Add-Stage 'tool-plan' 'FAIL' 'TOOL_DISCOVERY_FAILED'
    } else {
        Add-Stage 'tool-plan' 'PASS' "dotnet_portable=$needDotnet,node_portable=$needNode"
        if ($needDotnet -or $needNode) {
            if ($NoBootstrap) {
                Add-Stage 'tool-apply' 'FAIL' 'BOOTSTRAP_DISABLED'
            } else {
                Run-Stage 'tool-apply-portable' {
                    $bootstrap = Join-Path $PSScriptRoot 'bootstrap-chatgpt-windows-local.ps1'
                    $script:Tooling.bootstrap = . $bootstrap -ProjectRoot $PublicRepoRoot -NeedDotnet:$needDotnet -NeedNode:$needNode -ForcePortable:$ForcePortableTools
                    if ($needDotnet) { $script:Tooling.dotnet_origin = 'project-local-verified' }
                    if ($needNode) { $script:Tooling.node_origin = 'project-local-verified' }
                } | Out-Null
            }
        } else {
            Add-Stage 'tool-apply-portable' 'SKIP' 'EXISTING_TOOLING_REUSABLE'
        }
    }
    $hasDotnet = Run-Stage 'tool-verify-dotnet10' {
        $found = Discover-DotnetSdk10
        if (-not $found) { throw 'SDK10_UNAVAILABLE_AFTER_APPLY' }
        $script:Tooling.dotnet_after = $found
        Write-Host "Verified SDK $found"
    }
    $hasNode = $true
    if ($IncludePrivateBridge) {
        $hasNode = Run-Stage 'tool-verify-node22' {
            $found = Discover-Node22
            if (-not $found) { throw 'NODE22_UNAVAILABLE_AFTER_APPLY' }
            $script:Tooling.node_after = $found
            Write-Host "Verified Node $found"
        }
    }
    if ($BootstrapOnly) {
        Add-Stage 'qualification-batch' 'SKIP' 'BOOTSTRAP_ONLY'
        return
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

                if (-not $RunLiveGuest -or $SkipLiveGuest) {
                    Add-Stage 'real-anonymous-guest' 'SKIP' 'DEFAULT_NO_EXTERNAL_SIDE_EFFECT'
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
        $ready = if ($hasNode) {
            Run-Stage 'private-bridge-tool-preflight' {
                Require-Command 'git'
                Require-Command 'node'
                if (-not (Discover-Node22)) { throw 'NODE22_REQUIRED' }
                Write-Host 'Using existing Git credential manager or SSH configuration, not gh CLI'
            }
        } else {
            Add-Stage 'private-bridge-tool-preflight' 'SKIP' 'NODE22_NOT_AVAILABLE'
            $false
        }
        if ($ready) {
            $privateRoot = Join-Path $WorkRoot 'private-bridge-checkout'
            $cloned = Run-Stage 'pinned-private-bridge-checkout' {
                & git clone --depth 1 --branch $PrivateBranch 'https://github.com/SupraShellScripts/userscripts-private.git' $privateRoot
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
                        $containerReady = $false
                        foreach ($candidate in @('docker', 'podman', 'nerdctl')) {
                            if (-not (Get-Command $candidate -ErrorAction SilentlyContinue)) { continue }
                            try {
                                & $candidate info *> $null
                                if ($LASTEXITCODE -eq 0) { $containerReady = $true; break }
                            } catch { }
                        }
                        if (-not $containerReady -and (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
                            try {
                                $distros = @(& wsl.exe --list --quiet 2>$null |
                                    ForEach-Object { ($_ -replace [char]0, '').Trim() } |
                                    Where-Object { $_ })
                                foreach ($distro in $distros) {
                                    foreach ($candidate in @('docker', 'podman', 'nerdctl')) {
                                        & wsl.exe -d $distro -- sh -lc "command -v $candidate >/dev/null 2>&1 && $candidate info >/dev/null 2>&1" *> $null
                                        if ($LASTEXITCODE -eq 0) { $containerReady = $true; break }
                                    }
                                    if ($containerReady) { break }
                                }
                            } catch { }
                        }
                        if ($containerReady) {
                            Add-Stage 'container-engine-discovery' 'PASS' 'USABLE_RUNTIME_CONFIRMED'
                        } else {
                            Add-Stage 'container-engine-discovery' 'SKIP' 'NO_USABLE_CONTAINER_RUNTIME'
                        }
                                                if ($containerReady) {
                            Run-Stage 'private-bridge-heavy-browser-ci' {
                                & (Join-Path $privateRoot 'ci.ps1') heavy -Script chatgpt-local-bridge
                            } | Out-Null
                        } else {
                            Add-Stage 'private-bridge-heavy-browser-ci' 'SKIP' 'CONTAINER_ENGINE_UNAVAILABLE'
                            # No privileged hypervisor/daemon installer is authorized.
                            # A native Playwright fixture rep can still qualify the
                            # browser path without being called canonical heavy PASS.
                        }

                        Run-Stage 'private-bridge-native-browser-smoke' {
                            $bootstrap = Join-Path $PSScriptRoot 'bootstrap-chatgpt-windows-local.ps1'
                            $null = . $bootstrap -ProjectRoot $PublicRepoRoot
                            $toolOwnedRoot = Join-Path $PublicRepoRoot '.local/suprachat-qualification'
                            $nodePkgRoot = Join-Path $toolOwnedRoot 'native-playwright'
                            $playwrightManifest = Join-Path $nodePkgRoot 'node_modules/playwright/package.json'
                            $installPlaywright = $true
                            if (Test-Path -LiteralPath $playwrightManifest) {
                                $metadata = Get-Content -Raw -LiteralPath $playwrightManifest | ConvertFrom-Json
                                if ($metadata.version -eq '1.62.0') { $installPlaywright = $false }
                            }
                            if ($installPlaywright) {
                                $nodeBin = (Get-Command node -ErrorAction Stop).Source
                                $npmCli = Join-Path (Split-Path -Parent $nodeBin) 'node_modules/npm/bin/npm-cli.js'
                                if (-not (Test-Path -LiteralPath $npmCli)) { throw 'NPM_CLI_UNAVAILABLE' }
                                New-Item -ItemType Directory -Force -Path $nodePkgRoot | Out-Null
                                & node $npmCli install --prefix $nodePkgRoot --ignore-scripts --no-audit --no-fund --save-exact 'playwright@1.62.0'
                                if ($LASTEXITCODE -ne 0) { throw 'PLAYWRIGHT_NPM_INSTALL_FAILED' }
                            }
                            if (-not (Test-Path -LiteralPath $playwrightManifest) -or
                                (Get-Content -Raw -LiteralPath $playwrightManifest | ConvertFrom-Json).version -ne '1.62.0') {
                                throw 'PLAYWRIGHT_PIN_VERIFICATION_FAILED'
                            }
                            $cli = Join-Path $nodePkgRoot 'node_modules/playwright/cli.js'
                            if (-not (Test-Path -LiteralPath $cli)) { throw 'PLAYWRIGHT_CLI_NOT_FOUND' }
                            & node $cli install chromium firefox webkit
                            if ($LASTEXITCODE -ne 0) { throw 'PLAYWRIGHT_BROWSERS_INSTALL_FAILED' }
                            $runner = Join-Path $PSScriptRoot 'run-bridge-native-browser-smoke.mjs'
                            $nativeReceipt = Join-Path $WorkRoot 'native-bridge-browser-smoke.json'
                            & node $runner --private-root $privateRoot --playwright-root $nodePkgRoot --receipt $nativeReceipt --browsers chromium,firefox,webkit
                            if ($LASTEXITCODE -ne 0) { throw 'NATIVE_BRIDGE_BROWSER_SMOKE_FAILED' }
                        } | Out-Null
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
        guest_probe_enabled = [bool]($RunLiveGuest -and -not $SkipLiveGuest)
        workspace_probe_enabled = $true
        discover_only = [bool]$DiscoverOnly
        bootstrap_only = [bool]$BootstrapOnly
        tooling = $script:Tooling
        stages = @($script:Stages)
    }
    $summary = Join-Path $WorkRoot 'summary.json'
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summary -Encoding utf8
    Write-Host ""
    Write-Host 'LOCAL QUALIFICATION COMPLETE.'
    Write-Host "Safe to share: $summary"
    Write-Host 'Do not share the private checkout or arbitrary browser files.'
    Get-Content -Raw $summary | Write-Host
    if (@($script:Stages | Where-Object { $_.state -eq 'FAIL' }).Count -gt 0) {
        Write-Host 'One or more stages failed; see the stage reason codes in summary.json.'
        exit 1
    }
}