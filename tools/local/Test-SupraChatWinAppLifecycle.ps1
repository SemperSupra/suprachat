param(
    [switch]$FirstPartyMetadataHil,
    [Parameter(Mandatory)][string]$WinApp,
    [Parameter(Mandatory)][string]$SummaryDirectory,
    [string]$ReceiptDirectory,
    [Parameter(Mandatory)][ValidateSet('Expiry','Stop','Navigation','ReviewClose','OwnerClose')][string]$Case,
    [Parameter(Mandatory)][int]$TargetProcessId,
    [Parameter(Mandatory)][long]$MainWindow,
    [long]$ReviewWindow,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$AssemblyHash
)
$ErrorActionPreference = 'Stop'
if (!$FirstPartyMetadataHil) { throw 'EXPLICIT_FIRST_PARTY_HIL_OPT_IN_REQUIRED' }
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'POWERSHELL_7_REQUIRED' }
. (Join-Path $PSScriptRoot 'Read-NativeMetadataUiState.ps1')
if (![IO.Path]::IsPathFullyQualified($SummaryDirectory)) { throw 'ABSOLUTE_SUMMARY_DIRECTORY_REQUIRED' }
if (!$ReceiptDirectory) { $ReceiptDirectory = $SummaryDirectory }
if (![IO.Path]::IsPathFullyQualified($ReceiptDirectory)) { throw 'ABSOLUTE_RECEIPT_DIRECTORY_REQUIRED' }
$winapp = (Get-Command $WinApp -ErrorAction Stop).Source
$evidence = [IO.Path]::GetFullPath($SummaryDirectory)
$receipts = [IO.Path]::GetFullPath($ReceiptDirectory)
New-Item -ItemType Directory -Path $receipts -Force | Out-Null
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
$run = [Guid]::NewGuid().ToString('N')
$env:WINAPP_UI_WORKFLOW_ID = 'suprachat-metadata-hil-' + $run
if ((& $winapp --version | Out-String).Trim() -ne '0.7.1') { throw 'WINAPP_VERSION_NOT_QUALIFIED' }
$started = [DateTime]::UtcNow
$stages = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = 'INCOMPLETE'
$reviewAdmitted = $false
$failureStopRequested = $false
function Invoke-WinApp([string[]]$Arguments) {
    $output = & $winapp @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'WINAPP_COMMAND_REFUSED_OR_OUTCOME_UNKNOWN' }
    return (($output | ForEach-Object { $_.ToString() }) -join "`n") | ConvertFrom-Json
}
function Read-State {
    $reply = Invoke-WinApp @('ui','get-value','Native.Metadata.Status','-w',"$ReviewWindow",'--json')
    if ($reply.text -isnot [string]) { throw 'UI_STATUS_CONTRACT_REFUSED' }
    return Convert-NativeMetadataUiState $reply.text
}
function Assert-ReviewBinding {
    $current = Get-Process -Id $TargetProcessId -ErrorAction Stop
    if ($current.StartTime.ToUniversalTime().Ticks -ne $targetStartTicks) { throw 'REVIEW_WINDOW_BINDING_REFUSED' }
    $windows = @(Invoke-WinApp @('ui','list-windows','-a',"$TargetProcessId",'--json'))
    $matching = @($windows | Where-Object {$_.hwnd -eq $ReviewWindow -and $_.processId -eq $TargetProcessId -and
        $_.title -eq 'Review ChatGPT metadata experiment' -and $_.ownerHwnd -eq $MainWindow})
    if ($matching.Count -ne 1) { throw 'REVIEW_WINDOW_BINDING_REFUSED' }
}
function Record-State([string]$Label, $State) {
    $stages.Add([ordered]@{stage=$Label;observed_at_utc=[DateTime]::UtcNow.ToString('o');
        capture_active=[bool]$State.consent.capture_active;observer_attached=[bool]$State.observer_attached;
        attaching=[bool]$State.attaching;end_reason=$State.end_reason;cleanup_complete=[bool]$State.cleanup_complete;
        enable_completed=[bool]$State.enable_completed;disable_completed=[bool]$State.disable_completed;
        network_events=$State.native_events;diagnostic_incidents=$State.native_loss})
}
function Assert-Off($State) {
    if ($State.consent.capture_active -or $State.observer_attached -or $State.attaching) { throw 'CAPTURE_NOT_OFF' }
}
function Action([string]$Selector, [string]$Verb = 'invoke', [long]$Window = $ReviewWindow) {
    $null = Invoke-WinApp @('ui','invoke',$Selector,'-w',"$Window",'--action',$Verb,'--json')
}
function Save-Receipt([string]$Path, $Value) {
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $writer = [IO.StreamWriter]::new($stream)
    try { $writer.Write(($Value | ConvertTo-Json -Depth 8)) } finally { $writer.Dispose() }
}
try {
    $process = Get-Process -Id $TargetProcessId
    $targetStartTicks = $process.StartTime.ToUniversalTime().Ticks
    $main = Invoke-WinApp @('ui','status','-w',"$MainWindow",'--json')
    if ($main.processId -ne $process.Id -or $main.windowTitle -ne 'SupraChat') { throw 'MAIN_WINDOW_BINDING_REFUSED' }
    if (!$ReviewWindow) {
        $control = Invoke-WinApp @('ui','get-property','Native.Metadata.Review','-w',"$MainWindow",'--property','IsEnabled','--json')
        if ($control.properties.IsEnabled -ne 'True') { throw 'REVIEW_CONTROL_UNAVAILABLE' }
        Action 'Native.Metadata.Review' 'invoke' $MainWindow
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $windows = @(Invoke-WinApp @('ui','list-windows','-a',"$TargetProcessId",'--json'))
            $reviews = @($windows | Where-Object {$_.title -eq 'Review ChatGPT metadata experiment' -and $_.ownerHwnd -eq $MainWindow})
            if (!$reviews.Count) { Start-Sleep -Milliseconds 200 }
        } while (!$reviews.Count -and [DateTime]::UtcNow -lt $deadline)
        if ($reviews.Count -ne 1) { throw 'ONE_REVIEW_WINDOW_REQUIRED' }
        $ReviewWindow = $reviews[0].hwnd
    }
    $review = Invoke-WinApp @('ui','status','-w',"$ReviewWindow",'--json')
    if ($review.processId -ne $process.Id -or $review.windowTitle -ne 'Review ChatGPT metadata experiment') { throw 'REVIEW_WINDOW_BINDING_REFUSED' }
    Assert-ReviewBinding
    $reviewAdmitted = $true;
    $before = @(Get-ChildItem $evidence -Filter 'suprachat-metadata-summary-*.json' | ForEach-Object FullName)
    Action 'Native.Metadata.Acknowledge' 'toggle-on'
    Action 'Native.Metadata.Prepare'
    $confirm = Invoke-WinApp @('ui','get-property','Native.Metadata.Confirm','-w',"$ReviewWindow",'--property','IsEnabled','--json')
    if ($confirm.properties.IsEnabled -ne 'True') { throw 'FRESH_REVIEW_NOT_READY' }
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    Action 'Native.Metadata.Confirm'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do { $state = Read-State; if (!$state.consent.capture_active -or $state.attaching) { Start-Sleep -Milliseconds 200 } }
    while ((!$state.consent.capture_active -or $state.attaching) -and [DateTime]::UtcNow -lt $deadline)
    if (!$state.consent.capture_active -or !$state.observer_attached -or $state.attaching) { throw 'CAPTURE_DID_NOT_START' }
    Record-State 'ACTIVE_BEFORE_ACTION' $state
    $expected = switch ($Case) { Expiry {'LeaseExpired'} Stop {'OperatorStop'} Navigation {'Navigation'} default {'ReviewClosed'} }
    if ($Case -eq 'Stop') { Action 'Native.Metadata.Stop' }
    elseif ($Case -eq 'Navigation') {
        $reload = Invoke-WinApp @('ui','get-property','Nav.Reload','-w',"$MainWindow",'--property','IsEnabled','--json')
        if ($reload.properties.IsEnabled -ne 'True') { throw 'RELOAD_UNAVAILABLE' }
        Action 'Nav.Reload' 'invoke' $MainWindow
    }
    elseif ($Case -eq 'Expiry') {
        while ($elapsed.Elapsed.TotalSeconds -lt 115) { Start-Sleep -Milliseconds 500 }
        $state = Read-State
        if ($elapsed.Elapsed.TotalSeconds -ge 120) { throw 'PRE_DEADLINE_OBSERVATION_TOO_LATE' }
        if (!$state.consent.capture_active -or !$state.observer_attached -or $state.attaching) { throw 'PRE_DEADLINE_CAPTURE_NOT_ACTIVE' }
        Record-State 'ACTIVE_BEFORE_TWO_MINUTE_DEADLINE' $state
        $stages.Add([ordered]@{stage='PRE_DEADLINE_MONOTONIC_CHECK';elapsed_seconds=$elapsed.Elapsed.TotalSeconds})
        while ($elapsed.Elapsed.TotalSeconds -lt 125) { Start-Sleep -Milliseconds 500 }
    }
    else {
        $target = if ($Case -eq 'OwnerClose') { $MainWindow } else { $ReviewWindow }
        # v0.7.1 includes owned windows in lookup. Bind the shallow native title bar to
        # the exact returned HWND; never confuse owner and review Close controls.
        $inspection = Invoke-WinApp @('ui','inspect','-w',"$target",'--depth','2','--json')
        $exactWindows = @($inspection.windows | Where-Object {$_.hwnd -eq $target})
        if ($exactWindows.Count -ne 1) { throw 'CLOSE_CONTROL_BINDING_REFUSED' }
        $rootId = if ($Case -eq 'OwnerClose') { 'SupraChat.MainWindow' } else { 'Native.Metadata.Window' }
        $roots = @($exactWindows[0].elements | Where-Object {$_.automationId -eq $rootId})
        if ($roots.Count -ne 1) { throw 'CLOSE_CONTROL_BINDING_REFUSED' }
        $bars = @($roots[0].children | Where-Object {$_.automationId -eq 'TitleBar' -and $_.type -eq 'TitleBar'})
        $close = @($bars.children | Where-Object {$_.automationId -eq 'Close' -and $_.type -eq 'Button' -and $_.isEnabled})
        if ($close.Count -ne 1 -or !$close[0].selector) { throw 'CLOSE_CONTROL_BINDING_REFUSED' }
        $null = Invoke-WinApp @('ui','invoke',$close[0].selector,'-w',"$target",'--action','invoke','--json')
    }
    if ($Case -in @('Expiry','Stop','Navigation')) {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do { $state = Read-State; if ($state.consent.capture_active -or $state.observer_attached -or $state.attaching) { Start-Sleep -Milliseconds 200 } }
        while (($state.consent.capture_active -or $state.observer_attached -or $state.attaching) -and [DateTime]::UtcNow -lt $deadline)
        Assert-Off $state
        $expectedUiReason = switch ($Case) { Expiry {'LEASE_EXPIRED'} Stop {'OPERATOR_STOP'} Navigation {'NAVIGATION'} }
        if ($state.end_reason -ne $expectedUiReason) { throw 'FIRST_END_REASON_MISMATCH' }
        Record-State 'OFF_AFTER_ACTION' $state
        if ($Case -eq 'Expiry') { $stages.Add([ordered]@{stage='POST_DEADLINE_MONOTONIC_CHECK';elapsed_seconds=$elapsed.Elapsed.TotalSeconds}) }
        Action 'Native.Metadata.Save'
    }
    elseif ($Case -eq 'OwnerClose') {
        if (!$process.WaitForExit(20000)) { throw 'OWNER_PROCESS_DID_NOT_EXIT' }
        $stages.Add([ordered]@{stage='OWNER_PROCESS_EXITED';observed_at_utc=[DateTime]::UtcNow.ToString('o');independent_process_observation=$true})
    }
    else {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $windows = @(Invoke-WinApp @('ui','list-windows','-a',"$TargetProcessId",'--json'))
            $reviewPresent = @($windows | Where-Object {$_.hwnd -eq $ReviewWindow}).Count -gt 0
            if ($reviewPresent) { Start-Sleep -Milliseconds 200 }
        } while ($reviewPresent -and [DateTime]::UtcNow -lt $deadline)
        if ($reviewPresent -or !@($windows | Where-Object {$_.hwnd -eq $MainWindow}).Count) { throw 'REVIEW_CLOSE_NOT_INDEPENDENTLY_OBSERVED' }
        $stages.Add([ordered]@{stage='REVIEW_GONE_OWNER_PRESENT';observed_at_utc=[DateTime]::UtcNow.ToString('o');independent_uia_observation=$true})
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $created = @(Get-ChildItem $evidence -Filter 'suprachat-metadata-summary-*.json' | Where-Object {$_.FullName -notin $before})
        if (!$created.Count) { Start-Sleep -Milliseconds 200 }
    } while (!$created.Count -and [DateTime]::UtcNow -lt $deadline)
    if ($created.Count -ne 1) { throw 'ONE_FRESH_AUTOMATIC_SUMMARY_REQUIRED' }
    $validationPath = Join-Path $receipts "winapp-$Case-validation-$run.json"
    $null = & (Join-Path $PSScriptRoot 'Test-NativeMetadataSummary.ps1') -SummaryPath $created[0].FullName -ExpectedEndReason $expected -ExpectedAssemblyHash $AssemblyHash -ReceiptPath $validationPath
    $validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
    $passed = $true; $failure = 'NONE'
}
catch {
    # Never emit UI text, browser errors, authority identifiers or account details.
    $known = @('WINAPP_COMMAND_REFUSED_OR_OUTCOME_UNKNOWN','EXACT_ORIGIN_SCOPE_REQUIRED','CAPTURE_NOT_OFF',
        'MAIN_WINDOW_BINDING_REFUSED','REVIEW_WINDOW_BINDING_REFUSED','REVIEW_CONTROL_UNAVAILABLE','ONE_REVIEW_WINDOW_REQUIRED',
        'FRESH_REVIEW_NOT_READY','CAPTURE_DID_NOT_START','RELOAD_UNAVAILABLE','FIRST_END_REASON_MISMATCH',
        'OWNER_PROCESS_DID_NOT_EXIT','REVIEW_CLOSE_NOT_INDEPENDENTLY_OBSERVED','ONE_FRESH_AUTOMATIC_SUMMARY_REQUIRED','CLOSE_CONTROL_BINDING_REFUSED',
        'PRE_DEADLINE_OBSERVATION_TOO_LATE','PRE_DEADLINE_CAPTURE_NOT_ACTIVE','UI_STATUS_CONTRACT_REFUSED')
    $failure = if ($_.Exception.Message -in $known) { $_.Exception.Message } else { 'VALIDATION_OR_AUTOMATION_INCOMPLETE' }
    throw "Native lifecycle qualification incomplete: $failure"
}
finally {
    if (!$passed -and $reviewAdmitted) {
        try { Assert-ReviewBinding; Action 'Native.Metadata.Stop'; $failureStopRequested = $true } catch { }
    }
    $receipt = [ordered]@{schema='suprachat-winapp-first-party-lifecycle/v1';case=$Case;passed=$passed;failure=$failure;
        started_at_utc=$started.ToString('o');finished_at_utc=[DateTime]::UtcNow.ToString('o');winapp_version='0.7.1';
        assembly_sha256=$AssemblyHash;stages=$stages.ToArray();aggregate_contract_valid=($passed -and $validation.aggregate_contract_valid);
        first_party_network_metadata_observed=($passed -and $validation.first_party_network_metadata_observed);
        observed_network_events=$(if($passed){$validation.observed_network_events}else{$null});completeness='UNKNOWN';
        signed_in_evidence='SEPARATE_UI_INDICATORS_ONLY';provider_operation_verified=$false;human_identity_verified=$false;
        credentials_read=$false;raw_browser_content_exported=$false;automatic_upload=$false;failure_stop_requested=$failureStopRequested}
    $receiptPath = Join-Path $receipts "winapp-$Case-lifecycle-$run.json"
    Save-Receipt $receiptPath $receipt
    $receipt | ConvertTo-Json -Depth 8
}
