param([string]$DotNet = 'dotnet', [Parameter(Mandatory = $true)][string]$ReceiptPath, [switch]$Consent)
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
$labStart.ArgumentList.Add($(if ($Consent) { '--metadata-consent-receipt' } else { '--shared-desktop-receipt' }))
$labStart.ArgumentList.Add($labReceipt)
$labProcess = [Diagnostics.Process]::Start($labStart)
$labPassed = $false
$labPhase = 'window'
$script:labSelectionWaits = 0
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
    function Select-LabSession([string]$name) {
        $combo = Get-LabControl 'Shared.Sessions'
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $item = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            try {
                $matches = $labWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
                foreach ($candidate in $matches) {
                    $selectionPattern = $null
                    if ($candidate.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) {
                        $item = $candidate; break
                    }
                }
            }
            catch { if (!$_.Exception.InnerException -or $_.Exception.InnerException.HResult -ne -2147418113) { throw } }
            if (!$item) { $script:labSelectionWaits++; Start-Sleep -Milliseconds 100 }
        } while (!$item -and [DateTime]::UtcNow -lt $deadline)
        if (!$item) { throw 'Shared session selection item unavailable.' }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    function Get-ConsentStatus {
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $text = Get-LabValue 'Consent.Status'
            if (![string]::IsNullOrWhiteSpace($text)) {
                $value = $text | ConvertFrom-Json
                if ($value.state -and $value.state -ne 'UNAVAILABLE') { return $value }
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'Consent status unavailable.'
    }
    function Start-ReviewedConsent {
        Invoke-Lab 'Consent.Preview'
        if ((Get-LabControl 'Consent.Confirm').Current.IsEnabled) { throw 'Consent confirmation enabled without acknowledgement.' }
        (Get-LabControl 'Consent.Acknowledge').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Invoke-Lab 'Consent.Confirm'
        if (!(Get-ConsentStatus).capture_active) { throw 'Reviewed synthetic consent did not start capture.' }
    }
    if ($Consent) {
        $labPhase = 'consent-default-off'
        $state = Get-ConsentStatus
        if ($state.capture_active -or $state.retained_events -ne 0 -or (Get-LabControl 'Consent.Confirm').Current.IsEnabled) {
            throw 'Consent was not off by default.'
        }
        Invoke-Lab 'Consent.Emit'
        if ((Get-LabValue 'Consent.Action') -ne 'Event refused') { throw 'An unapproved event was accepted.' }
        Invoke-Lab 'Consent.Preview'
        if ((Get-LabControl 'Consent.Confirm').Current.IsEnabled) { throw 'Unacknowledged consent was actionable.' }
        Select-LabSession 'Session B'
        if ((Get-LabControl 'Consent.Confirm').Current.IsEnabled -or (Get-ConsentStatus).capture_active) { throw 'Session switch retained a review.' }
        Select-LabSession 'Session A'
        $labPhase = 'consent-reviewed-start-stop'
        Start-ReviewedConsent
        Invoke-Lab 'Consent.Emit'
        if ((Get-ConsentStatus).retained_events -ne 1) { throw 'Approved event was not recorded.' }
        Invoke-Lab 'Consent.Stop'
        Invoke-Lab 'Consent.Emit'
        if ((Get-ConsentStatus).capture_active -or (Get-LabValue 'Consent.Action') -ne 'Event refused') { throw 'Stop did not block events.' }
        Invoke-Lab 'Consent.FirstParty'
        $review = Get-LabValue 'Consent.Review' | ConvertFrom-Json
        if ($review.available -or (Get-LabControl 'Consent.Confirm').Current.IsEnabled -or (Get-ConsentStatus).capture_active) {
            throw 'First-party admission was enabled by the prototype.'
        }
        Select-LabSession 'Session B'
        Invoke-Lab 'Consent.Emit'
        $labPhase = 'consent-expiry-view-change'
        Start-ReviewedConsent
        Invoke-Lab 'Consent.Expire'
        Invoke-Lab 'Consent.Emit'
        if ((Get-ConsentStatus).state -ne 'EXPIRED' -or (Get-LabValue 'Consent.Action') -ne 'Event refused') { throw 'Expiry did not block events.' }
        Start-ReviewedConsent
        Invoke-Lab 'Consent.Emit'
        Invoke-Lab 'Consent.ReplaceView'
        Invoke-Lab 'Consent.Emit'
        $state = Get-ConsentStatus
        if ($state.capture_active -or $state.view_revision -ne 1 -or $state.retained_events -ne 1 -or
            (Get-LabValue 'Consent.Action') -ne 'Event refused') { throw 'View replacement retained capture.' }
        Select-LabSession 'Session A'
    }
    $labPhase = 'gui-mutation'
    (Get-LabControl 'Shared.Title').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('gui-one')
    Invoke-Lab 'Shared.Save'
    Wait-LabStatus 'Title saved'
    $view = Get-LabValue 'Shared.Snapshot' | ConvertFrom-Json
    if ($view.version -ne 1 -or $view.title -ne 'gui-one' -or $view.trace_events -ne 1) { throw 'GUI mutation projection disagreed.' }

    $labPhase = 'enrolled-clients'
    Invoke-Lab 'Shared.Clients'
    Wait-LabStatus 'All enrolled clients completed'
    $view = Get-LabValue 'Shared.Snapshot' | ConvertFrom-Json
    if ($view.version -ne 3 -or $view.title -ne 'agent-updated') { throw 'GUI, automation and agent disagreed on session A.' }

    $labPhase = 'agent-gui-projection'
    Select-LabSession 'Session B'
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
    $expectedSchema = if ($Consent) { 'suprachat-metadata-consent-rdte/v1' } else { 'suprachat-shared-desktop-rdte/v1' }
    if ($labProcess.ExitCode -ne 0 -or !$receipt.passed -or $receipt.schema -ne $expectedSchema -or $receipt.phase -ne 'VERIFIED' -or
        $receipt.details.shared_sessions -ne 2 -or $receipt.details.gui_mutations -ne 1 -or
        $receipt.details.enrolled_peer_runs -ne 4 -or !$receipt.details.same_session_agent_after_automation -or !$receipt.details.same_session_replay_conflict_stale_qualified -or !$receipt.details.common_cursor -or
        !$receipt.details.durable_revocation_recovered -or !$receipt.details.metadata_recovered -or
        !$receipt.details.trace_session_routing -or $receipt.details.versions[0] -ne 3 -or $receipt.details.versions[1] -ne 1 -or
        $receipt.provider_actions -or $receipt.first_party_capture -or $receipt.human_identity_qualified -or
        $receipt.production_shared_host_qualified -or $receipt.production_issuer_custody_qualified -or $receipt.payload_exported) {
        throw 'Shared lab receipt qualification failed.'
    }
    if ($Consent -and (!$receipt.details.consent.synthetic_only -or !$receipt.details.consent.stopped -or
        !$receipt.details.consent.preview_cancelled -or !$receipt.details.consent.view_invalidated -or
        !$receipt.details.consent.lease_expired -or !$receipt.details.consent.first_party_refused -or
        !$receipt.details.consent.machine_status_agrees -or $receipt.details.consent.reviewed_starts -ne 3 -or
        $receipt.details.consent.refused_observations -lt 5)) { throw 'Consent receipt contract failed.' }
    $labPassed = $true
    $labPhase = 'VERIFIED'
    'Shared desktop UIA / automation / agent session composition PASS (synthetic metadata only).'
}
finally {
    if (!$labProcess.HasExited) { $labProcess.Kill($true); $null = $labProcess.WaitForExit(3000) }
    $labProcess.Dispose()
    $output = [IO.FileStream]::new($uiaReceipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.StreamWriter]::new($output)
    try { $writer.Write((@{ schema='suprachat-shared-desktop-uia/v1'; passed=$labPassed; phase=$labPhase; own_fixture_only=$true; metadata_consent_mode=[bool]$Consent; popup_waits=$script:labSelectionWaits; signed_in_capture=$false } | ConvertTo-Json)) }
    finally { $writer.Dispose() }
}
