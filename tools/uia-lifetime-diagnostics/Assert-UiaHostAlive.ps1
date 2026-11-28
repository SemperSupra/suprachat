#requires -Version 5.1
# Fail closed at every product UIA liveness boundary. No raw process content.
function Assert-UiaHostAlive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][AllowNull()]$Process,
        [Parameter(Mandatory=$true)]
        [ValidateSet('RootSearch','RootBoundary','InitialTree','InitialPeerRead','PeerBoundary','SemanticPeers','FinalReceipt')]
        [string]$Phase
    )
    if ($null -eq $Process) { throw 'UIA_HOST_LIFETIME_UNAVAILABLE' }
    try {
        $Process.Refresh()
        $exited = $Process.HasExited
        if ($exited -isnot [bool]) { throw 'INVALID_LIFETIME_OBSERVATION' }
    } catch {
        throw 'UIA_HOST_LIFETIME_UNAVAILABLE'
    }
    if (-not $exited) { return }
    $exitCode = 'unavailable'
    try { $exitCode = [string][int]$Process.ExitCode } catch {}
    Write-Host ('UIA host lifetime: processAlive=False exitCode=' + $exitCode)
    Write-Host ('UIA_LIFETIME_FAILURE_PHASE=' + $Phase)
    switch ($Phase) {
        'RootSearch'      { throw 'UIA_HOST_PROCESS_EXITED_BEFORE_ROOT' }
        'RootBoundary'    { throw 'UIA_HOST_PROCESS_EXITED_BEFORE_ROOT' }
        'InitialTree'     { throw 'UIA_HOST_PROCESS_EXITED_DURING_INITIAL_TREE' }
        'InitialPeerRead' { throw 'UIA_HOST_PROCESS_EXITED_DURING_INITIAL_TREE' }
        'PeerBoundary'    { throw 'UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS' }
        'SemanticPeers'   { throw 'UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS' }
        'FinalReceipt'    { throw 'UIA_HOST_PROCESS_EXITED_BEFORE_FINAL_RECEIPT' }
    }
}
