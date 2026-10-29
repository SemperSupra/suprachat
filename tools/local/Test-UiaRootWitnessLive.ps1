param(
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [Parameter(Mandatory)][string]$ObserverExecutable
)
$ErrorActionPreference='Stop'
if (!$IsWindows) { throw 'WINDOWS_REQUIRED' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$fixture=(Resolve-Path -LiteralPath $FixtureExecutable).Path
$observerExe=(Resolve-Path -LiteralPath $ObserverExecutable).Path
$owned=$null
$observer=$null
$receiptFile=Join-Path $env:RUNNER_TEMP ('suprachat-root-receipt-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    # Placeholder-only, independent native observer against a disposable owned window.
    # No first-party account, web content, browser profile or credentials.
    $owned=Start-Process -FilePath $fixture -ArgumentList @('--variant','placeholder') -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    $root=$null
    do {
        $owned.Refresh()
        if ($owned.HasExited) { throw 'FIXTURE_EXITED' }
        if ($owned.MainWindowHandle -ne [IntPtr]::Zero) {
            try {
                $candidate=[System.Windows.Automation.AutomationElement]::FromHandle($owned.MainWindowHandle)
                if ($candidate.Current.ProcessId -eq $owned.Id -and
                    $candidate.Current.AutomationId -ceq 'SupraChat.SyntheticTabProbe') {
                    $ready=$candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.PropertyCondition]::new(
                            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                            'Synthetic.TabProbe.Ready'))
                    if ($ready -and $ready.Current.Name -ceq 'PLACEHOLDER_READY') { $root=$candidate }
                }
            } catch { } # Initial transient readiness only; final readiness is mandatory.
        }
        if (!$root) { Start-Sleep -Milliseconds 100 }
    } while (!$root -and [DateTime]::UtcNow -lt $deadline)
    if (!$root) { throw 'OWNED_ROOT_NOT_READY' }
    $tab=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'Synthetic.TabProbe.SecondTab'))
    if (!$tab) { throw 'SECOND_TAB_NOT_FOUND' }
    $selection=$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    if (!$selection.Current.IsSelected) { throw 'SECOND_TAB_NOT_SELECTED' }
    $handle=$owned.MainWindowHandle.ToInt64()
    if ($handle -le 0) { throw 'INVALID_OWNED_HANDLE' }
    $start=[Diagnostics.ProcessStartInfo]::new($observerExe)
    $start.ArgumentList.Add([string]$owned.Id)
    $start.ArgumentList.Add([string]$handle)
    $start.ArgumentList.Add('--synthetic-tab-probe')
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $observer=[Diagnostics.Process]::Start($start)
    if (!$observer.WaitForExit(12000)) {
        $observer.Kill()
        $null=$observer.WaitForExit(2000)
        throw 'OBSERVER_WATCHDOG_EXPIRED'
    }
    $raw=$observer.StandardOutput.ReadToEnd()
    if ($raw.Length -gt 16384 -or [string]::IsNullOrWhiteSpace($raw)) {
        throw 'FIXED_RECEIPT_BOUND'
    }
    [IO.File]::WriteAllText($receiptFile,$raw,[Text.UTF8Encoding]::new($false))
    $validation=& (Join-Path $PSScriptRoot 'Test-UiaRootWitness.ps1') -ReceiptPath $receiptFile | ConvertFrom-Json
    if (!$validation.valid -or $validation.schema -cne 'suprachat-uia-root-receipt-validation/v1') {
        throw 'STRICT_RECEIPT_REJECTED'
    }
    Write-Host ('FIXED_ROOT_OUTCOME=' + $validation.outcome)
    Write-Host ('FIXED_ROOT_STAGE=' + $validation.stage)
    if ($validation.outcome -cne 'OBSERVED_ROOT' -or $observer.ExitCode -ne 0) {
        throw 'NATIVE_ROOT_NOT_VERIFIED'
    }
    $r=$raw|ConvertFrom-Json
    if (!$r.process_alive -or $r.os_window_valid -ne $true -or
        $r.os_pid_matches -ne $true -or $r.uia_pid_matches -ne $true -or
        $r.uia_root_id_matches -ne $true -or
        $r.known_controls.'Synthetic.TabProbe.Toolbar' -ne $true -or
        $r.known_controls.'Synthetic.TabProbe.SecondTab' -ne $true -or
        $r.known_controls.'Synthetic.TabProbe.SecondButton' -ne $true) {
        throw 'OWNED_SEMANTIC_PEER_REQUIRED'
    }
    Write-Host 'NATIVE_ROOT_FIXED_RECEIPT_AND_OWNED_PEERS_PASS'
}
finally {
    if ($observer) {
        try { if (!$observer.HasExited) { $observer.Kill(); $null=$observer.WaitForExit(2000) } }
        catch { }
        $observer.Dispose()
    }
    if ($owned) {
        try { if (!$owned.HasExited) { Stop-Process -Id $owned.Id -Force } }
        catch { }
        $owned.Dispose()
    }
    Remove-Item -LiteralPath $receiptFile -Force -ErrorAction SilentlyContinue
}
