param([switch]$SelfTest)
function Convert-NativeMetadataUiState([string]$Json) {
    if ($Json.Length -gt 32768) { throw 'UI_STATUS_CONTRACT_REFUSED' }
    $document = [System.Text.Json.JsonDocument]::Parse($Json)
    try {
        function Assert-UiFields($Element, [string[]]$Allowed) {
            if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'UI_STATUS_CONTRACT_REFUSED' }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if ($property.Name -cnotin $Allowed -or !$seen.Add($property.Name)) { throw 'UI_STATUS_CONTRACT_REFUSED' }
            }
            if ($seen.Count -ne $Allowed.Count) { throw 'UI_STATUS_CONTRACT_REFUSED' }
        }
        function Read-UiBoolean($Element) {
            if ($Element.ValueKind -notin @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) { throw 'UI_STATUS_CONTRACT_REFUSED' }
            return $Element.GetBoolean()
        }
        function Read-UiCount($Element) {
            $count = 0L
            if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or !$Element.TryGetInt64([ref]$count) -or $count -lt 0) { throw 'UI_STATUS_CONTRACT_REFUSED' }
            return $count
        }
        $root = $document.RootElement
        Assert-UiFields $root @('consent','observer_attached','attaching','native_events','native_loss','scope','completeness',
            'provider_operation_verified','diagnostics','end_reason','cleanup_complete','enable_completed','disable_completed')
        $consent = $root.GetProperty('consent')
        Assert-UiFields $consent @('session_id','view_revision','state','capture_active','retained_events','refused_observations','first_party_admission')
        if ($root.GetProperty('scope').GetString() -cne 'SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN' -or
            $root.GetProperty('completeness').GetString() -cne 'UNKNOWN' -or
            (Read-UiBoolean ($root.GetProperty('provider_operation_verified')))) { throw 'UI_STATUS_CONTRACT_REFUSED' }
        $ending = $root.GetProperty('end_reason').GetString()
        if ($ending -cnotin @('NONE','OPERATOR_STOP','SUMMARY_SAVE','LEASE_EXPIRED','NAVIGATION','VIEW_REPLACED','ORIGIN_UNAVAILABLE',
            'REVIEW_CLOSED','ATTACH_FAILURE','RECORDER_STOPPED','RECORDER_UNAVAILABLE','OBSERVER_FAILURE','INACTIVE_UNKNOWN')) { throw 'UI_STATUS_CONTRACT_REFUSED' }
        # Project only validated scalars. Session/actor identifiers and diagnostic contents never leave this parser.
        return [pscustomobject]@{
            consent=[pscustomobject]@{capture_active=Read-UiBoolean ($consent.GetProperty('capture_active'))}
            observer_attached=Read-UiBoolean ($root.GetProperty('observer_attached'));attaching=Read-UiBoolean ($root.GetProperty('attaching'))
            native_events=Read-UiCount ($root.GetProperty('native_events'));native_loss=Read-UiCount ($root.GetProperty('native_loss'))
            end_reason=$ending;cleanup_complete=Read-UiBoolean ($root.GetProperty('cleanup_complete'))
            enable_completed=Read-UiBoolean ($root.GetProperty('enable_completed'));disable_completed=Read-UiBoolean ($root.GetProperty('disable_completed'))
        }
    } finally { $document.Dispose() }
}
if ($SelfTest) {
    $sample = [ordered]@{consent=[ordered]@{session_id='synthetic-private-status-sentinel';view_revision=0;state='STOPPED';capture_active=$false;retained_events=0;refused_observations=0;first_party_admission=$true};
        observer_attached=$false;attaching=$false;native_events=0;native_loss=0;scope='SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN';completeness='UNKNOWN';
        provider_operation_verified=$false;diagnostics=$null;end_reason='OPERATOR_STOP';cleanup_complete=$true;enable_completed=$true;disable_completed=$true}
    $base = $sample | ConvertTo-Json -Depth 6 -Compress
    $result = Convert-NativeMetadataUiState $base
    if (($result | ConvertTo-Json -Depth 4).Contains('synthetic-private-status-sentinel')) { throw 'UI_STATUS_PROJECTION_LEAKED' }
    $negative = @(
        $base.Replace('"observer_attached":false','"observer_attached":"false"'),
        $base.Replace('"native_events":0','"native_events":{"private":"synthetic-private-status-sentinel"}'),
        $base.Replace('"OPERATOR_STOP"','"synthetic-private-status-sentinel"'),
        $base.Replace('"attaching":false,',''),
        $base.Replace('"native_events":0','"native_events":0,"native_events":0'),
        $base.Replace('"native_events":0','"native_events":-1'),
        $base.Replace('"provider_operation_verified":false','"provider_operation_verified":true'),
        $base.Replace('"cleanup_complete":true','"cleanup_complete":null'),
        $base.Replace('SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN','UNADMITTED_SCOPE'))
    foreach ($json in $negative) {
        $refused = $false
        try { $null = Convert-NativeMetadataUiState $json } catch { $refused = $true }
        if (!$refused) { throw 'UI_STATUS_NEGATIVE_CASE_FAILED' }
    }
    @{schema='suprachat-ui-status-synthetic-selftest/v1';passed=$true;cases=10;live_capture=$false} | ConvertTo-Json
}
