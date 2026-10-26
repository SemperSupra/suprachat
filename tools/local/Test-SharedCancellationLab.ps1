param([string]$DotNet = 'dotnet', [Parameter(Mandatory = $true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
$fixtureRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$fixtureDll = Join-Path $fixtureRoot 'SupraChat.PeerLedgerRdte/bin/Release/net10.0/SupraChat.PeerLedgerRdte.dll'
$receipt = [IO.Path]::GetFullPath($ReceiptPath)
$uiaReceipt = $receipt + '.uia.json'
if ((Test-Path -LiteralPath $receipt) -or (Test-Path -LiteralPath $uiaReceipt)) { throw 'Use fresh fixture receipt paths.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receipt)) | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command $DotNet -ErrorAction Stop).Source
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.ArgumentList.Add($fixtureDll); $start.ArgumentList.Add('--shared-cancellation-receipt'); $start.ArgumentList.Add($receipt)
$fixture = [Diagnostics.Process]::Start($start)
$passed = $false; $phase = 'window'
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $fixture.Refresh()
        if ($fixture.HasExited) { throw 'Synthetic cancellation fixture exited before UI qualification.' }
        if ($fixture.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($fixture.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Synthetic cancellation window unavailable.' }
    function Get-CancellationControl([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $control = $null
            try {
                $window = [System.Windows.Automation.AutomationElement]::FromHandle($fixture.MainWindowHandle)
                $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            } catch { if (!$_.Exception.InnerException -or $_.Exception.InnerException.HResult -ne -2147418113) { throw } }
            if (!$control) { Start-Sleep -Milliseconds 100 }
        } while (!$control -and [DateTime]::UtcNow -lt $deadline)
        if (!$control -or [string]::IsNullOrWhiteSpace($control.Current.Name)) { throw 'Synthetic cancellation semantic control missing.' }
        return $control
    }
    function Invoke-CancellationControl([string]$id) {
        $control = Get-CancellationControl $id
        if (!$control.Current.IsEnabled) { throw 'Synthetic cancellation control not ready.' }
        $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Wait-CancellationStatus([string]$expected) {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $value = (Get-CancellationControl 'CancellationLab.Status').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            if ($value -eq $expected) { return }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'Synthetic cancellation operation did not complete.'
    }
    $phase = 'gui-review'
    if ((Get-CancellationControl 'CancellationLab.Confirm').Current.IsEnabled) { throw 'Cancellation enabled before review.' }
    Invoke-CancellationControl 'CancellationLab.Preview'; Wait-CancellationStatus 'Review ready'
    $phase = 'gui-local-signal'
    Invoke-CancellationControl 'CancellationLab.Confirm'; Wait-CancellationStatus 'Local synthetic cancellation verified'
    $projection = (Get-CancellationControl 'CancellationLab.Snapshot').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value | ConvertFrom-Json
    if ($projection[0].status -ne 'VERIFIED_LOCAL_SIGNAL' -or !$projection[0].local_signal_verified -or $projection[0].receipt_version -ne 1) { throw 'GUI cancellation projection disagreed.' }
    $phase = 'scoped-observed-clients'
    Invoke-CancellationControl 'CancellationLab.Clients'; Wait-CancellationStatus 'Scoped clients completed'
    $projection = (Get-CancellationControl 'CancellationLab.Snapshot').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value | ConvertFrom-Json
    if (@($projection | Where-Object { $_.status -ne 'VERIFIED_LOCAL_SIGNAL' -or !$_.local_signal_verified -or $_.receipt_version -ne 1 }).Count -ne 0 -or $projection.Count -ne 3) { throw 'Shared cancellation projections disagreed.' }
    $phase = 'durable-recovery'
    Invoke-CancellationControl 'CancellationLab.Verify'
    if (!$fixture.WaitForExit(15000)) { throw 'Synthetic cancellation recovery did not finish.' }
    $json = [IO.File]::ReadAllText($receipt)
    foreach ($forbidden in @('Bearer ', 'http://', 'https://', 'S-1-5-', 'synthetic-human', 'synthetic-automation', 'synthetic-agent')) {
        if ($json.Contains($forbidden)) { throw 'Synthetic cancellation receipt privacy oracle failed.' }
    }
    $result = $json | ConvertFrom-Json
    if ($fixture.ExitCode -ne 0 -or !$result.passed -or $result.phase -ne 'VERIFIED' -or
        $result.schema -ne 'suprachat-shared-synthetic-cancellation/v1' -or $result.scope -ne 'DISPOSABLE_SHARED_SYNTHETIC_CANCELLATION' -or
        !$result.details.gui_reviewed_local_signal -or !$result.details.automation_local_signal -or !$result.details.agent_local_signal -or
        !$result.details.metadata_scope_refused -or !$result.details.cross_session_refused -or !$result.details.forged_fields_refused -or
        !$result.details.replay_stable -or !$result.details.revocation_recovered -or !$result.details.outcomes_recovered -or
        !$result.details.replacement_not_cancelled -or !$result.details.one_ledger_gate -or !$result.details.one_checkpoint_authority -or
        $result.details.observed_peer_runs -ne 4 -or $result.details.provider_stop_verified -or $result.details.callback_completion_verified -or
        $result.real_provider_actions -or $result.first_party_capture -or
        $result.human_identity_qualified -or $result.production_host_qualified -or $result.production_issuer_custody_qualified -or
        $result.grants_exported -or $result.signing_key_exported -or $result.checkpoint_exported -or
        $result.peer_identity_values_exported -or $result.provider_stop_verified) { throw 'Synthetic cancellation receipt did not qualify.' }
    $passed = $true; $phase = 'VERIFIED'
    'Shared cancellation GUI/UIA, automation JSON and agent JSON-RPC PASS (synthetic only).'
}
finally {
    if (!$fixture.HasExited) { $fixture.Kill($true); $null = $fixture.WaitForExit(3000) }
    $fixture.Dispose()
    $output = [IO.FileStream]::new($uiaReceipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.StreamWriter]::new($output)
    try { $writer.Write((@{schema='suprachat-shared-cancellation-uia/v1';passed=$passed;phase=$phase;own_fixture_only=$true;synthetic_only=$true;signed_in_capture=$false} | ConvertTo-Json)) }
    finally { $writer.Dispose() }
}
