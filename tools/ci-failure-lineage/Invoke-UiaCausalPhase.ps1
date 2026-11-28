#requires -Version 5.1
# Disposable, public signed-out Windows runner only. No tool/provider actions.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Collector,
    [Parameter(Mandatory=$true)][ValidateSet('Idle','Root','Descendants')][string]$Probe,
    [Parameter(Mandatory=$true)][ValidateSet('StartupOnly','Redirected')][string]$Launch
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'DISPOSABLE_PUBLIC_WINDOWS_RUNNER_REQUIRED'
}
$exePath = (Resolve-Path -LiteralPath $Exe).Path
$collectorPath = (Resolve-Path -LiteralPath $Collector).Path
$dir = Join-Path $env:RUNNER_TEMP ('uia-phase-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $dir
$oldStartup = $env:SUPRACHAT_STARTUP_LOG
$previous = $null
$p = $null
$clock = [Diagnostics.Stopwatch]::StartNew()
$phase = 'WARM_LAUNCH'
$rootSeen = $false
$rootAt = $null
$descendants = $null
$namedPeerCount = $null
$traversalCompleted = $false
$observedExit = $null
$observerFault = $false
$startupReceipt = $null
$status = 'NOT_OBSERVED'
$startup = Join-Path $dir 'probe.startup'
$stdout = Join-Path $dir 'probe.stdout'
$stderr = Join-Path $dir 'probe.stderr'
function Stop-Owned([Diagnostics.Process]$child) {
    if ($null -ne $child -and -not $child.HasExited) {
        Stop-Process -Id $child.Id -Force -ErrorAction Stop
        if (-not $child.WaitForExit(5000)) { throw 'OWNED_PROCESS_CLEANUP_TIMEOUT' }
    }
}
function Set-Phase([string]$next) {
    $script:phase = $next
    Write-Host ('UIA_PHASE=' + $next)
}
try {
    # Separate fresh runner for every case; no full packaging/install workload.
    $env:SUPRACHAT_STARTUP_LOG = Join-Path $dir 'warm.startup'
    $warmArgs = @{FilePath=$exePath; PassThru=$true;
        RedirectStandardOutput=(Join-Path $dir 'warm.stdout');
        RedirectStandardError=(Join-Path $dir 'warm.stderr')}
    $previous = Start-Process @warmArgs
    Start-Sleep -Seconds 8
    if ($previous.HasExited) {
        $observedExit = $previous.ExitCode
        $status = 'WARM_PROCESS_EXITED'
        $startup = Join-Path $dir 'warm.startup'
        $stdout = Join-Path $dir 'warm.stdout'
        $stderr = Join-Path $dir 'warm.stderr'
    }
    else {
        Stop-Owned $previous
        Set-Phase 'PROBE_LAUNCH'
        $env:SUPRACHAT_STARTUP_LOG = $startup
        if ($Launch -eq 'Redirected') {
            $p = Start-Process -FilePath $exePath -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        }
        else {
            # Historical GUI WinExe mode: no pipe attachment or console capture.
            $p = Start-Process -FilePath $exePath -PassThru
        }
        $clock.Restart()
        if ($Probe -ne 'Idle') {
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            Set-Phase 'ROOT_SEARCH'
            $root = $null
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
            while ($clock.Elapsed.TotalSeconds -lt 20 -and -not $p.HasExited -and -not $rootSeen) {
                Start-Sleep -Milliseconds 500
                $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,$condition)
                if ($candidate -and $candidate.Current.ProcessId -eq $p.Id -and
                    $candidate.Current.AutomationId -eq 'SupraChat.MainWindow') {
                    $root = $candidate
                    $rootSeen = $true
                    $rootAt = [int]$clock.ElapsedMilliseconds
                }
            }
            if ($rootSeen -and -not $p.HasExited -and $Probe -eq 'Descendants') {
                # Intentionally exercise the historical full-tree query once.
                Set-Phase 'RAW_DESCENDANTS'
                $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)
                $descendants = [Math]::Min(4001,$all.Count)
                if ($all.Count -gt 4000) {
                    $status = 'TRAVERSAL_BOUND_EXCEEDED'
                }
                else {
                    Set-Phase 'SEMANTIC_ID_READ'
                    $namedPeerCount = 0
                    foreach ($peer in $all) {
                        if (-not [string]::IsNullOrWhiteSpace($peer.Current.AutomationId)) {
                            $namedPeerCount++
                        }
                    }
                    $traversalCompleted = $true
                }
            }
        }
        if ($status -eq 'NOT_OBSERVED') {
            if (-not $p.HasExited) {
                Set-Phase 'SURVIVAL_OBSERVATION'
                while ($clock.Elapsed.TotalSeconds -lt 20 -and -not $p.HasExited) {
                    Start-Sleep -Milliseconds 100
                }
            }
            if ($p.HasExited) {
                $observedExit = $p.ExitCode
                $status = 'PROBE_PROCESS_EXITED'
            }
            elseif ($Probe -ne 'Idle' -and -not $rootSeen) {
                $status = 'ALIVE_NO_ROOT_UNQUALIFIED'
            }
            elseif ($Probe -eq 'Descendants' -and (-not $traversalCompleted -or $namedPeerCount -eq 0)) {
                $status = 'ALIVE_NO_SEMANTIC_PEERS_UNQUALIFIED'
            }
            else {
                $status = 'OBSERVED_ALIVE'
            }
        }
    }
}
catch {
    $observerFault = $true
    $status = 'OBSERVER_FAULT'
    # Do not output exception text, window names, paths or provider data.
}
finally {
    if ($null -ne $p -and $p.HasExited) {
        $observedExit = $p.ExitCode
        $status = 'PROBE_PROCESS_EXITED'
    }
    try {
        $raw = & $collectorPath -StartupLog $startup -StdoutLog $stdout -StderrLog $stderr
        $candidateReceipt = $raw | ConvertFrom-Json
        if ($candidateReceipt.schema -eq 'suprachat-redacted-uia-startup-observation/v2') {
            $startupReceipt = $candidateReceipt
        }
    } catch {}
    $cleanupOk = $true
    try { Stop-Owned $p; Stop-Owned $previous } catch { $cleanupOk = $false }
    $env:SUPRACHAT_STARTUP_LOG = $oldStartup
    try { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop } catch { $cleanupOk = $false }
    [ordered]@{
        schema = 'suprachat-uia-causal-phase/v1'
        source_sha = '824147915e7fba498a9b94d9ec22075fe3dd082a'
        app_sha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
        probe = $Probe
        launch_mode = $Launch
        last_phase = $phase
        status = $status
        root_observed = $rootSeen
        root_elapsed_ms = $rootAt
        elapsed_ms = [int]$clock.ElapsedMilliseconds
        descendant_count_capped = $descendants
        semantic_peer_count = $namedPeerCount
        traversal_completed = $traversalCompleted
        exit_code = $observedExit
        observer_fault = $observerFault
        startup_observation = $startupReceipt
        cleanup_ok = $cleanupOk
        root_cause = 'UNKNOWN'
        product_uia_passed = $false
        release_gate_waived = $false
    } | ConvertTo-Json -Depth 6 -Compress
    if (-not $cleanupOk) { throw 'PHASE_CLEANUP_FAILED' }
}
if ($status -ne 'OBSERVED_ALIVE') { throw 'CAUSAL_PHASE_NOT_SUCCESSFUL' }
