param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'

# Parse only the bounded, application-authored UIA lifecycle projection. Never
# return raw JSON/UI text, identifiers or exception content in qualification receipts.
function Convert-NativeLifecycleReadback([string]$Json) {
    if ($Json.Length -gt 16000) { throw 'LIFECYCLE_READBACK_REFUSED' }
    $document = [System.Text.Json.JsonDocument]::Parse($Json)
    try {
        function Require-Fields($Object, [string[]]$Names) {
            if ($Object.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'LIFECYCLE_FIELDS_REFUSED' }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($field in $Object.EnumerateObject()) {
                if ($field.Name -cnotin $Names -or !$seen.Add($field.Name)) { throw 'LIFECYCLE_FIELDS_REFUSED' }
            }
            if ($seen.Count -ne $Names.Length) { throw 'LIFECYCLE_FIELDS_REFUSED' }
        }
        function Read-Boolean($Value) {
            if ($Value.ValueKind -notin @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) {
                throw 'LIFECYCLE_BOOLEAN_REFUSED'
            }
            return $Value.GetBoolean()
        }
        $root = $document.RootElement
        Require-Fields $root @('schema','build_version','completeness','is_authoritative_for_consent','events')
        if ($root.GetProperty('schema').GetString() -cne 'suprachat-native-lifecycle-readback/v1' -or
            $root.GetProperty('completeness').GetString() -cne 'UNKNOWN' -or
            (Read-Boolean ($root.GetProperty('is_authoritative_for_consent')))) { throw 'LIFECYCLE_SCOPE_REFUSED' }
        $version = $root.GetProperty('build_version').GetString()
        if ($version -notmatch '^(UNAVAILABLE|[0-9]+(?:\.[0-9]+){1,3})$') { throw 'LIFECYCLE_BUILD_REFUSED' }
        $items = $root.GetProperty('events')
        if ($items.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $items.GetArrayLength() -lt 1 -or
            $items.GetArrayLength() -gt 24) { throw 'LIFECYCLE_BOUND_REFUSED' }
        $actions = @('Opened','Prepared','Confirmed','Refused','StopRequested','SaveRequested','SummarySaved','SaveFailed','CloseRequested','Closed','Observed')
        $states = @('ACTIVE','OFF','STOPPED','EXPIRED','VIEW_CHANGED','RECORDER_STOPPED','RECORDER_UNAVAILABLE','UNKNOWN')
        $ends = @('None','OperatorStop','SummarySave','LeaseExpired','Navigation','ViewReplaced','OriginUnavailable','ReviewClosed',
            'AttachFailure','RecorderStopped','RecorderUnavailable','ObserverFailure','InactiveUnknown','Unknown')
        $prior = -1L
        $latest = $null
        foreach ($entry in $items.EnumerateArray()) {
            Require-Fields $entry @('ElapsedMs','Action','State','EndReason','Active','ObserverAttached','CleanupComplete','EnableCompleted','DisableCompleted')
            $offset = 0L
            if ($entry.GetProperty('ElapsedMs').ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                !$entry.GetProperty('ElapsedMs').TryGetInt64([ref]$offset) -or $offset -lt $prior) {
                throw 'LIFECYCLE_OFFSET_REFUSED'
            }
            $prior = $offset
            $action = $entry.GetProperty('Action').GetString()
            $state = $entry.GetProperty('State').GetString()
            $end = $entry.GetProperty('EndReason').GetString()
            if ($action -cnotin $actions -or $state -cnotin $states -or $end -cnotin $ends) {
                throw 'LIFECYCLE_ENUM_REFUSED'
            }
            $active = Read-Boolean ($entry.GetProperty('Active'))
            $observer = Read-Boolean ($entry.GetProperty('ObserverAttached'))
            $cleanup = Read-Boolean ($entry.GetProperty('CleanupComplete'))
            $enabled = Read-Boolean ($entry.GetProperty('EnableCompleted'))
            $disabled = Read-Boolean ($entry.GetProperty('DisableCompleted'))
            if ($active -and ($end -cne 'None' -or $state -cne 'ACTIVE')) { throw 'LIFECYCLE_ACTIVE_INCONSISTENT' }
            $latest = [pscustomobject]@{action=$action;state=$state;end_reason=$end;
                active=$active;observer_attached=$observer;cleanup_complete=$cleanup;
                enable_completed=$enabled;disable_completed=$disabled}
        }
        return [pscustomobject]@{schema='suprachat-sanitized-lifecycle-projection/v1';entries=$items.GetArrayLength();
            last_elapsed_ms=$prior;latest=$latest;completeness='UNKNOWN';source_is_authoritative=$false}
    } finally { $document.Dispose() }
}
if ($SelfTest) {
    $sample = [ordered]@{schema='suprachat-native-lifecycle-readback/v1';build_version='1.0.0.0';
        completeness='UNKNOWN';is_authoritative_for_consent=$false;
        events=@([ordered]@{ElapsedMs=0;Action='Opened';State='UNKNOWN';EndReason='None';Active=$false;
            ObserverAttached=$false;CleanupComplete=$false;EnableCompleted=$false;DisableCompleted=$false},
            [ordered]@{ElapsedMs=200;Action='Observed';State='STOPPED';EndReason='OperatorStop';Active=$false;
                ObserverAttached=$false;CleanupComplete=$true;EnableCompleted=$true;DisableCompleted=$true})}
    $good = $sample | ConvertTo-Json -Depth 10 -Compress
    $result = Convert-NativeLifecycleReadback $good
    if ($result.entries -ne 2 -or $result.latest.end_reason -cne 'OperatorStop') { throw 'LIFECYCLE_TEST_FAILED' }
    $negative = @(
        $good.Replace('"entries":','"unexpected_private_session":"synthetic-secret","entries":'),
        $good.Replace('"Active":false','"Active":true'),
        $good.Replace('"ElapsedMs":200','"ElapsedMs":-1'),
        $good.Replace('"OperatorStop"','"synthetic-secret"'),
        $good.Replace('"Action":"Observed"','"Action":"Invalid"'),
        $good.Replace('"is_authoritative_for_consent":false','"is_authoritative_for_consent":true'),
        $good.Replace('"ElapsedMs":200','"ElapsedMs":200,"ElapsedMs":200')
    )
    foreach ($case in $negative) {
        $refused = $false
        try { $null = Convert-NativeLifecycleReadback $case } catch { $refused = $true }
        if (!$refused) { throw 'LIFECYCLE_NEGATIVE_TEST_FAILED' }
    }
    @{schema='suprachat-lifecycle-readback-selftest/v1';passed=$true;cases=8;live_capture=$false} | ConvertTo-Json
}
