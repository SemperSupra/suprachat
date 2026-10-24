param([string]$DotNet = 'dotnet', [Parameter(Mandatory = $true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
$labRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$labDll = Join-Path $labRoot 'SupraChat.PeerLedgerRdte/bin/Release/net10.0/SupraChat.PeerLedgerRdte.dll'
$labReceipt = [IO.Path]::GetFullPath($ReceiptPath)
$uiaReceipt = $labReceipt + '.uia.json'
if ((Test-Path -LiteralPath $labReceipt) -or (Test-Path -LiteralPath $uiaReceipt)) { throw 'Use fresh receipt paths.' }
$labStart = [Diagnostics.ProcessStartInfo]::new()
$labStart.FileName = (Get-Command $DotNet -ErrorAction Stop).Source
$labStart.UseShellExecute = $false
$labStart.CreateNoWindow = $true
$labStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$labStart.ArgumentList.Add($labDll)
$labStart.ArgumentList.Add('--shared-desktop-receipt')
$labStart.ArgumentList.Add($labReceipt)
$labProcess = [Diagnostics.Process]::Start($labStart)
$labPassed = $false
$labPhase = 'window'
try {
    $labWindow = $null
    $labDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $labProcess.Refresh()
        if ($labProcess.HasExited) { throw 'Shared lab exited before UI qualification.' }
        if ($labProcess.MainWindowHandle -ne [IntPtr]::Zero) {
            $labWindow = [System.Windows.Automation.AutomationElement]::FromHandle($labProcess.MainWindowHandle)
        }
        if (!$labWindow) { Start-Sleep -Milliseconds 100 }
    } while (!$labWindow -and [DateTime]::UtcNow -lt $labDeadline)
    if (!$labWindow) { throw 'Shared lab UI unavailable.' }

    function Get-LabControl([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $control = $null
            try { $control = $labWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) }
            catch { if (!$_.Exception.InnerException -or $_.Exception.InnerException.HResult -ne -2147418113) { throw } }
            if (!$control) { Start-Sleep -Milliseconds 100 }
        } while (!$control -and [DateTime]::UtcNow -lt $deadline)
        if (!$control -or [string]::IsNullOrWhiteSpace($control.Current.Name)) { throw "Shared semantic peer missing: $id" }
        return $control
    }
    function Get-LabValue([string]$id) {
        return (Get-LabControl $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    }
    function Invoke-Lab([string]$id) {
        (Get-LabControl $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Wait-LabStatus([string]$expected) {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ((Get-LabValue 'Shared.Status') -eq $expected) { return }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'Shared semantic operation did not complete.'
    }
    $labPhase = 'gui-mutation'
    (Get-LabControl 'Shared.Title').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('gui-one')
    Invoke-Lab 'Shared.Save'
    Wait-LabStatus 'Title saved'
    $view = Get-LabValue 'Shared.Snapshot' | ConvertFrom-Json
    if ($view.version -ne 1 -or $view.title -ne 'gui-one' -or $view.trace_events -ne 1) { throw 'GUI mutation projection disagreed.' }

    $labPhase = 'enrolled-clients'
    Invoke-Lab 'Shared.Clients'
    Wait-LabStatus 'Both clients completed'
    $view = Get-LabValue 'Shared.Snapshot' | ConvertFrom-Json
    if ($view.version -ne 2 -or $view.title -ne 'automation-updated') { throw 'Automation and GUI projections disagreed.' }

    $labPhase = 'agent-gui-projection'
    $combo = Get-LabControl 'Shared.Sessions'
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Session B')
    $item = $labWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (!$item) { throw 'Shared session selection item unavailable.' }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Lab 'Shared.Refresh'
    Wait-LabStatus 'Refreshed'
    $view = Get-LabValue 'Shared.Snapshot' | ConvertFrom-Json
    if ($view.session -ne 1 -or $view.version -ne 1 -or $view.title -ne 'agent-updated' -or $view.trace_events -ne 1) {
        throw 'Agent and GUI projections disagreed.'
    }
    $labPhase = 'revocation'
    Invoke-Lab 'Shared.Revoke'
    Wait-LabStatus 'Agent revoked; later read refused'
    $labPhase = 'recovery'
    Invoke-Lab 'Shared.Verify'
    if (!$labProcess.WaitForExit(15000)) { throw 'Shared lab recovery did not finish.' }
    $json = [IO.File]::ReadAllText($labReceipt)
    foreach ($forbidden in @('gui-one', 'automation-updated', 'agent-updated', 'Bearer ', 'http://', 'https://', 'S-1-5-')) {
        if ($json.Contains($forbidden)) { throw 'Shared lab receipt privacy oracle failed.' }
    }
    $receipt = $json | ConvertFrom-Json
    if ($labProcess.ExitCode -ne 0 -or !$receipt.passed -or $receipt.phase -ne 'VERIFIED' -or
        $receipt.details.shared_sessions -ne 2 -or $receipt.details.gui_mutations -ne 1 -or
        $receipt.details.enrolled_peer_runs -ne 3 -or !$receipt.details.common_cursor -or
        !$receipt.details.durable_revocation_recovered -or !$receipt.details.metadata_recovered -or
        !$receipt.details.trace_session_routing -or $receipt.details.versions[0] -ne 2 -or $receipt.details.versions[1] -ne 1 -or
        $receipt.provider_actions -or $receipt.first_party_capture -or $receipt.human_identity_qualified -or
        $receipt.production_shared_host_qualified -or $receipt.production_issuer_custody_qualified -or $receipt.payload_exported) {
        throw 'Shared lab receipt qualification failed.'
    }
    $labPassed = $true
    $labPhase = 'VERIFIED'
    'Shared desktop UIA / automation / agent session composition PASS (synthetic metadata only).'
}
finally {
    if (!$labProcess.HasExited) { $labProcess.Kill($true); $null = $labProcess.WaitForExit(3000) }
    $labProcess.Dispose()
    $output = [IO.FileStream]::new($uiaReceipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.StreamWriter]::new($output)
    try { $writer.Write((@{ schema='suprachat-shared-desktop-uia/v1'; passed=$labPassed; phase=$labPhase; own_fixture_only=$true; signed_in_capture=$false } | ConvertTo-Json)) }
    finally { $writer.Dispose() }
}
