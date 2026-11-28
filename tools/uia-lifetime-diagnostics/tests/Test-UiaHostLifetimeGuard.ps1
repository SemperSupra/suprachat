#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assert-UiaHostAlive.ps1')
$checks = 0
function New-ObservedProcess([bool]$Exited, [int]$Code = 70) {
    $process = [pscustomobject]@{ HasExited = $Exited; ExitCode = $Code; RefreshCalls = 0 }
    $process | Add-Member ScriptMethod Refresh { $this.RefreshCalls++ }
    return $process
}
function Require-Failure($Process, [string]$Phase, [string]$Expected) {
    $observed = $null
    try { Assert-UiaHostAlive -Process $Process -Phase $Phase }
    catch { $observed = $_.Exception.Message }
    if ($observed -cne $Expected) { throw 'LIFETIME_GUARD_NEGATIVE_FAILED' }
    $script:checks++
}
$expected = [ordered]@{
    RootSearch = 'UIA_HOST_PROCESS_EXITED_BEFORE_ROOT'
    RootBoundary = 'UIA_HOST_PROCESS_EXITED_BEFORE_ROOT'
    InitialTree = 'UIA_HOST_PROCESS_EXITED_DURING_INITIAL_TREE'
    InitialPeerRead = 'UIA_HOST_PROCESS_EXITED_DURING_INITIAL_TREE'
    PeerBoundary = 'UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS'
    SemanticPeers = 'UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS'
    FinalReceipt = 'UIA_HOST_PROCESS_EXITED_BEFORE_FINAL_RECEIPT'
}
foreach ($phase in $expected.Keys) {
    $alive = New-ObservedProcess $false
    Assert-UiaHostAlive -Process $alive -Phase $phase
    if ($alive.RefreshCalls -ne 1) { throw 'LIFETIME_GUARD_DID_NOT_REFRESH' }
    $checks++
    foreach ($code in @(0,70)) {
        $dead = New-ObservedProcess $true $code
        Require-Failure $dead $phase $expected[$phase]
        if ($dead.RefreshCalls -ne 1) { throw 'LIFETIME_GUARD_DID_NOT_REFRESH_DEAD' }
    }
}
# A stale cached live value must not be used after Refresh reports termination.
$stale = New-ObservedProcess $false
$stale | Add-Member ScriptMethod Refresh { $this.HasExited = $true } -Force
Require-Failure $stale RootBoundary 'UIA_HOST_PROCESS_EXITED_BEFORE_ROOT'
$stale = New-ObservedProcess $false
$stale | Add-Member ScriptMethod Refresh { $this.HasExited = $true } -Force
Require-Failure $stale PeerBoundary 'UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS'
$stale = New-ObservedProcess $false
$stale | Add-Member ScriptMethod Refresh { $this.HasExited = $true } -Force
Require-Failure $stale FinalReceipt 'UIA_HOST_PROCESS_EXITED_BEFORE_FINAL_RECEIPT'
$unreadable = New-ObservedProcess $false
$unreadable | Add-Member ScriptMethod Refresh { throw 'untrusted detail' } -Force
Require-Failure $unreadable RootSearch 'UIA_HOST_LIFETIME_UNAVAILABLE'
Require-Failure $null RootSearch 'UIA_HOST_LIFETIME_UNAVAILABLE'
# Real process witness, without launching the product or a child.
$current = Get-Process -Id $PID
Assert-UiaHostAlive -Process $current -Phase FinalReceipt
$checks++
[ordered]@{schema='suprachat-uia-lifetime-guard-test/v1';result='PASS';checks=$checks;
    actual_desktop_repaired=$false;release_gate_waived=$false} | ConvertTo-Json -Compress
