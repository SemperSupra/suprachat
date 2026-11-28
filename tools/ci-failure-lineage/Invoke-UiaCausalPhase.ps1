#requires -Version 5.1
# Disposable, public signed-out Windows runner only. No tool/provider actions.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Collector,
    [Parameter(Mandatory=$true)][ValidateSet('Idle','Root','Descendants','FirstOwned')][string]$Probe,
    [Parameter(Mandatory=$true)][ValidateSet('StartupOnly','Redirected')][string]$Launch
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'DISPOSABLE_PUBLIC_WINDOWS_RUNNER_REQUIRED'
}
$exePath = (Resolve-Path -LiteralPath $Exe).Path
$dllPath = Join-Path (Split-Path -Parent $exePath) 'SupraChat.dll'
$exeDigest = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
$dllDigest = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
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
$firstOwnedFound = $false
$firstOwnedElapsed = $null
$observerHResult = $null
$observerInnerHResult = $null
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
function Find-FirstOwnedRootTabs($root) {
    # Same bounded owned-control walk as the first RootTabs check in the
    # pinned full-product workflow. No selection, Invoke or provider input.
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $queue = New-Object System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]
    $queue.Enqueue($root)
    $visited = 0
    while ($queue.Count -gt 0) {
        $node = $queue.Dequeue()
        $child = $walker.GetFirstChild($node)
        while ($child) {
            if ($visited -ge 4000) { throw 'UIA_OWNED_TRAVERSAL_BUDGET_EXHAUSTED' }
            $visited++
            if ($child.Current.AutomationId -eq 'RootTabs') { return $true }
            if ($child.Current.FrameworkId -ne 'Chrome') { $queue.Enqueue($child) }
            $child = $walker.GetNextSibling($child)
        }
    }
    return $false
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
            if ($rootSeen -and -not $p.HasExited -and $Probe -in @('Descendants','FirstOwned')) {
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
            if ($Probe -eq 'FirstOwned' -and $traversalCompleted -and $namedPeerCount -gt 0 -and -not $p.HasExited) {
                Set-Phase 'FIRST_OWNED_ROOT_TABS'
                $p.Refresh()
                if (-not $p.HasExited -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
                    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
                }
                if (-not $p.HasExited -and $root -and $root.Current.ProcessId -eq $p.Id -and
                    $root.Current.AutomationId -eq 'SupraChat.MainWindow') {
                    $firstOwnedFound = Find-FirstOwnedRootTabs $root
                    $firstOwnedElapsed = [int]$clock.ElapsedMilliseconds
                }
                if (-not $firstOwnedFound -and -not $p.HasExited) {
                    $status = 'FIRST_OWNED_CONTROL_UNQUALIFIED'
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
            elseif ($Probe -in @('Descendants','FirstOwned') -and (-not $traversalCompleted -or $namedPeerCount -eq 0)) {
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
    # Numeric HRESULT observations only; no exception type/message/stack.
    try { $observerHResult = [int]$_.Exception.HResult } catch {}
    try { if ($null -ne $_.Exception.InnerException) { $observerInnerHResult = [int]$_.Exception.InnerException.HResult } } catch {}
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
        schema = 'suprachat-uia-causal-phase/v2'
        source_sha = '824147915e7fba498a9b94d9ec22075fe3dd082a'
        apphost_sha256 = $exeDigest
        managed_assembly_sha256 = $dllDigest
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
        first_owned_root_tabs_observed = $firstOwnedFound
        first_owned_elapsed_ms = $firstOwnedElapsed
        exit_code = $observedExit
        observer_fault = $observerFault
        observer_hresult = $observerHResult
        observer_inner_hresult = $observerInnerHResult
        startup_observation = $startupReceipt
        cleanup_ok = $cleanupOk
        root_cause = 'UNKNOWN'
        product_uia_passed = $false
        release_gate_waived = $false
    } | ConvertTo-Json -Depth 6 -Compress
    if (-not $cleanupOk) { throw 'PHASE_CLEANUP_FAILED' }
}
if ($status -ne 'OBSERVED_ALIVE') { throw 'CAUSAL_PHASE_NOT_SUCCESSFUL' }
