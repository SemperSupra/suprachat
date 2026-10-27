param(
    [string]$SummaryPath,
    [ValidateSet('OperatorStop', 'LeaseExpired', 'Navigation', 'SummarySave', 'ReviewClosed')]
    [string]$ExpectedEndReason = 'OperatorStop',
    [string]$ExpectedAssemblyHash,
    [string]$ReceiptPath,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

function Assert-MetadataFields($Element, [string[]]$Allowed, [bool]$RequireAll = $true) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'METADATA_OBJECT_REQUIRED' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $permitted = [Collections.Generic.HashSet[string]]::new([string[]]$Allowed, [StringComparer]::Ordinal)
    foreach ($field in $Element.EnumerateObject()) {
        if (!$permitted.Contains($field.Name) -or !$seen.Add($field.Name)) { throw 'METADATA_FIELD_CONTRACT_REFUSED' }
    }
    if ($RequireAll -and $seen.Count -ne $permitted.Count) { throw 'METADATA_REQUIRED_FIELD_MISSING' }
}
function Read-MetadataCount($Element) {
    $count = 0L
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or !$Element.TryGetInt64([ref]$count) -or $count -lt 0) {
        throw 'METADATA_COUNT_REFUSED'
    }
    return $count
}
function Test-MetadataDocument([string]$Json, [string]$Hash, [string]$Ending) {
    if ($Hash -notmatch '^[a-fA-F0-9]{64}$' -or $Json.Length -gt 32768) { throw 'METADATA_INPUT_BOUND_REFUSED' }
    $document = [System.Text.Json.JsonDocument]::Parse($Json)
    try {
        $root = $document.RootElement
        Assert-MetadataFields $root @('schema','evidence','scope','completeness','assembly_sha256',
            'observed_network_events','native_loss','capture_gap','native_loss_semantics','diagnostics',
            'capture_active','observer_attached','attaching','first_lease_end_reason','cleanup_complete',
            'enable_completed','disable_completed','stopped_interval_disclosed','total_evicted_events',
            'retained_events','lifecycle','signed_in_state','provider_operation_verified',
            'conversation_binding_verified','human_identity_verified','prompts_responses_headers_cookies_exported','automatic_upload')
        foreach ($pair in @(
            @('schema','suprachat-first-party-metadata-hil-summary/v2'),
            @('scope','SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN'),
            @('completeness','UNKNOWN'), @('native_loss_semantics','DIAGNOSTIC_INCIDENTS_NOT_LOST_EVENT_COUNT'),
            @('signed_in_state','OPERATOR_CHECK_REQUIRED'), @('first_lease_end_reason',$Ending))) {
            if ($root.GetProperty($pair[0]).ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                $root.GetProperty($pair[0]).GetString() -cne $pair[1]) { throw 'METADATA_SCOPE_OR_ENDING_REFUSED' }
        }
        $recordedHash = $root.GetProperty('assembly_sha256').GetString()
        if (![string]::Equals($recordedHash, $Hash, [StringComparison]::OrdinalIgnoreCase)) { throw 'METADATA_BUILD_MISMATCH' }
        foreach ($field in @('capture_active','observer_attached','attaching','provider_operation_verified',
            'conversation_binding_verified','human_identity_verified','prompts_responses_headers_cookies_exported','automatic_upload')) {
            if ($root.GetProperty($field).ValueKind -ne [System.Text.Json.JsonValueKind]::False) { throw 'METADATA_ACTIVE_OR_UNSUPPORTED_CLAIM' }
        }
        foreach ($field in @('cleanup_complete','enable_completed','disable_completed','stopped_interval_disclosed')) {
            if ($root.GetProperty($field).ValueKind -ne [System.Text.Json.JsonValueKind]::True) { throw 'METADATA_CLEANUP_NOT_QUALIFIED' }
        }
        if ($root.GetProperty('capture_gap').ValueKind -notin @([System.Text.Json.JsonValueKind]::False, [System.Text.Json.JsonValueKind]::True)) {
            throw 'METADATA_GAP_TYPE_REFUSED'
        }
        $observed = Read-MetadataCount ($root.GetProperty('observed_network_events'))
        $expectedEvidence = if ($observed -gt 0) { 'FIRST_PARTY_LIVE_REDACTED' } else { 'NO_FIRST_PARTY_OBSERVATIONS' }
        if ($root.GetProperty('evidence').GetString() -cne $expectedEvidence) { throw 'METADATA_EVIDENCE_COUNT_MISMATCH' }
        $retained = Read-MetadataCount ($root.GetProperty('retained_events'))
        $evicted = Read-MetadataCount ($root.GetProperty('total_evicted_events'))
        $incidents = Read-MetadataCount ($root.GetProperty('native_loss'))
        if ($retained -gt 256) { throw 'METADATA_OBSERVATION_BOUND_REFUSED' }
        $lifecycle = $root.GetProperty('lifecycle')
        Assert-MetadataFields $lifecycle @('Request','Response','Chunk','Completed','Cancelled','Reconnected','Error','CaptureGap') $false
        [decimal]$eventTotal = 0
        foreach ($field in $lifecycle.EnumerateObject()) { $eventTotal += Read-MetadataCount $field.Value }
        if ($eventTotal -ne $retained) { throw 'METADATA_EVENT_TOTAL_MISMATCH' }
        $diagnostics = $root.GetProperty('diagnostics')
        Assert-MetadataFields $diagnostics @('ObservationIncidents','LifecycleIncidents','Reasons','TotalIncidents')
        $reasons = $diagnostics.GetProperty('Reasons')
        $lifecycleReasons = @('AttachFailure','UnsubscribeFailure','EnableFailure','DisableFailure','CleanupTimeout')
        Assert-MetadataFields $reasons (@('RawEventOversize','MissingRequestId','RequestIdOversize','InvalidRequestUrl',
            'CorrelationCapacity','RequestScopeRevoked','MetadataMalformed','CallbackFailure','SinkRefused',
            'SinkUnauthorized','SinkDisposed','SinkFailure','NavigationFailure') + $lifecycleReasons) $false
        [decimal]$observationIncidents = 0; [decimal]$lifecycleIncidents = 0
        $closedReasons = [ordered]@{}
        foreach ($field in $reasons.EnumerateObject()) {
            $count = Read-MetadataCount $field.Value
            $closedReasons[$field.Name] = $count
            if ($field.Name -in $lifecycleReasons) { $lifecycleIncidents += $count } else { $observationIncidents += $count }
        }
        if ($observationIncidents -ne (Read-MetadataCount ($diagnostics.GetProperty('ObservationIncidents'))) -or
            $lifecycleIncidents -ne (Read-MetadataCount ($diagnostics.GetProperty('LifecycleIncidents'))) -or
            $observationIncidents + $lifecycleIncidents -ne $incidents -or
            $incidents -ne (Read-MetadataCount ($diagnostics.GetProperty('TotalIncidents')))) { throw 'METADATA_DIAGNOSTIC_TOTAL_MISMATCH' }
        return [ordered]@{ schema='suprachat-native-metadata-summary-validation/v2'; aggregate_contract_valid=$true;
            source_evidence=$expectedEvidence; first_party_network_metadata_observed=($observed -gt 0);
            lifecycle_case=$Ending; assembly_sha256=$Hash.ToLowerInvariant(); observed_network_events=$observed;
            retained_events=$retained; total_evicted_events=$evicted; diagnostic_incidents=$incidents;
            diagnostic_reasons=$closedReasons; capture_gap=$root.GetProperty('capture_gap').GetBoolean();
            capture_active=$false; observer_attached=$false; cleanup_complete=$true; completeness='UNKNOWN';
            signed_in_state='OPERATOR_CHECK_REQUIRED'; runtime_state_independently_observed=$false;
            provider_operation_verified=$false; conversation_binding_verified=$false; human_identity_verified=$false;
            public_upload_performed=$false }
    }
    finally { $document.Dispose() }
}

if ($SelfTest) {
    # In-memory synthetic schema examples only. No browser, profile or live observations.
    $hash = 'a' * 64
    $sample = [ordered]@{schema='suprachat-first-party-metadata-hil-summary/v2'; evidence='FIRST_PARTY_LIVE_REDACTED';
        scope='SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN'; completeness='UNKNOWN'; assembly_sha256=$hash;
        observed_network_events=1; native_loss=0; capture_gap=$true;
        native_loss_semantics='DIAGNOSTIC_INCIDENTS_NOT_LOST_EVENT_COUNT';
        diagnostics=@{ObservationIncidents=0;LifecycleIncidents=0;Reasons=@{};TotalIncidents=0};
        capture_active=$false;observer_attached=$false;attaching=$false;first_lease_end_reason='OperatorStop';
        cleanup_complete=$true;enable_completed=$true;disable_completed=$true;stopped_interval_disclosed=$true;
        total_evicted_events=0;retained_events=1;lifecycle=@{Request=1};signed_in_state='OPERATOR_CHECK_REQUIRED';
        provider_operation_verified=$false;conversation_binding_verified=$false;human_identity_verified=$false;
        prompts_responses_headers_cookies_exported=$false;automatic_upload=$false}
    $base = $sample | ConvertTo-Json -Depth 8 -Compress
    $null = Test-MetadataDocument $base $hash 'OperatorStop'
    foreach ($ending in @('LeaseExpired', 'Navigation', 'SummarySave', 'ReviewClosed')) {
        $case = $base.Replace('"OperatorStop"', ('"' + $ending + '"'))
        $result = Test-MetadataDocument $case $hash $ending
        if ($result.lifecycle_case -cne $ending) { throw 'SYNTHETIC_END_REASON_CHECK_FAILED' }
    }
    $idle = $base.Replace('"observed_network_events":1','"observed_network_events":0').Replace('"retained_events":1','"retained_events":0').Replace('"Request":1','"Request":0').Replace('FIRST_PARTY_LIVE_REDACTED','NO_FIRST_PARTY_OBSERVATIONS')
    $idleResult = Test-MetadataDocument $idle $hash 'OperatorStop'
    if ($idleResult.first_party_network_metadata_observed) { throw 'SYNTHETIC_IDLE_EVIDENCE_CHECK_FAILED' }
    $negative = @(
        $base.Replace('"capture_active":false','"capture_active":true'),
        $base.Replace('"cleanup_complete":true','"cleanup_complete":false'),
        $base.Replace('"OperatorStop"','"LeaseExpired"'),
        $base.Replace('"Reasons":{}','"Reasons":{"private-unexpected-field":1}'),
        $base.Replace('"native_loss":0','"native_loss":1'),
        $base.Replace('"schema":','"schema":"duplicate","schema":'))
    foreach ($json in $negative) {
        $refused = $false
        try { $null = Test-MetadataDocument $json $hash 'OperatorStop' } catch { $refused = $true }
        if (!$refused) { throw 'SYNTHETIC_VALIDATOR_NEGATIVE_CASE_FAILED' }
    }
    [ordered]@{evidence='SYNTHETIC_VALIDATOR_SELF_TEST';passed=$true;cases=12;live_capture=$false} | ConvertTo-Json
    return
}
if (!$SummaryPath -or !$ReceiptPath -or !$ExpectedAssemblyHash) { throw 'SUMMARY_HASH_AND_FRESH_RECEIPT_PATH_REQUIRED' }
if (Test-Path -LiteralPath $ReceiptPath) { throw 'PRESERVE_EXISTING_VALIDATION_RECEIPT' }
$source = Get-Item -LiteralPath $SummaryPath
if ($source.Length -gt 32768) { throw 'METADATA_FILE_BOUND_REFUSED' }
$result = Test-MetadataDocument ([IO.File]::ReadAllText($source.FullName)) $ExpectedAssemblyHash $ExpectedEndReason
$result.source_sha256 = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$result.recorded_at_utc = [DateTime]::UtcNow.ToString('o')
$stream = [IO.FileStream]::new([IO.Path]::GetFullPath($ReceiptPath), [IO.FileMode]::CreateNew)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 8))
    $stream.Write($bytes, 0, $bytes.Length)
} finally { $stream.Dispose() }
$result | ConvertTo-Json -Depth 8
