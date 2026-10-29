param([string]$ReceiptPath, [switch]$SelfTest)
$ErrorActionPreference='Stop'
function Assert-Fields($Element,[string[]]$Names) {
    if($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object){throw 'OBJECT_REQUIRED'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($p in $Element.EnumerateObject()){
        if($p.Name -cnotin $Names -or !$seen.Add($p.Name)){throw 'FIELD_REFUSED'}
    }
    if($seen.Count -ne $Names.Count){throw 'REQUIRED_FIELD_MISSING'}
}
function Read-Bool($Element,[string]$Name){
    $v=$Element.GetProperty($Name)
    if($v.ValueKind -notin @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)){throw 'BOOLEAN_REQUIRED'}
    return $v.GetBoolean()
}
function Read-Enum($Element,[string]$Name,[string[]]$Allowed){
    $v=$Element.GetProperty($Name)
    if($v.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $v.GetString() -cnotin $Allowed){throw 'ENUM_REFUSED'}
    return $v.GetString()
}
function Read-Count($Element,[string]$Name,[int]$Maximum){
    $n=0;$v=$Element.GetProperty($Name)
    if($v.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or !$v.TryGetInt32([ref]$n) -or $n -lt 0 -or $n -gt $Maximum){throw 'COUNT_REFUSED'}
    return $n
}
function Test-EventDocument([string]$Json){
    if(!$Json -or $Json.Length -gt 32768){throw 'INPUT_BOUND_REFUSED'}
    $doc=[System.Text.Json.JsonDocument]::Parse($Json)
    try{
        $r=$doc.RootElement
        $bools=@('passed','first_party_account_involved','raw_values_exported','owned_process_exit_confirmed','listener_ready',
            'name_positive_observed','value_positive_observed','public_value_mutation_verified','final_diagnostic_received',
            'sticky_disclosure_observed','callback_unknown','simulated_callback','sticky_disclosure_negative_passed',
            'negative_control_expected','negative_control_refused')
        Assert-Fields $r (@('schema','failure','observer','outcome')+$bools)
        $null=Read-Enum $r 'schema' @('suprachat-synthetic-uia-event-test/v1')
        $outcome=Read-Enum $r 'outcome' @('OBSERVED_SAFE','NOT_VERIFIED','DISCLOSURE_OBSERVED')
        $failure=Read-Enum $r 'failure' @('NONE','FIXTURE_UNAVAILABLE','FIXTURE_IDENTITY_REFUSED','EVENT_HANDSHAKE_REFUSED',
            'POST_REMOVAL_MUTATION_UNVERIFIED','EVENT_RECEIPT_REFUSED','EVENT_WITNESS_NOT_VERIFIED','OWNED_PROCESS_EXIT_UNCONFIRMED')
        $b=@{};foreach($name in $bools){$b[$name]=Read-Bool $r $name}
        if($b.first_party_account_involved -or $b.raw_values_exported){throw 'SCOPE_REFUSED'}
        $o=$r.GetProperty('observer');$observerDisclosure=$false;$observerPassed=$false
        if($o.ValueKind -ne [System.Text.Json.JsonValueKind]::Null){
            $observerBools=@('passed','listener_ready','positive_control_observed','fill_control_observed','value_control_observed',
                'value_fill_observed','legacy_event_source_independently_qualified','sensitive_value_disclosed','callback_unknown',
                'text_event_content_read','text_event_content_qualified','removal_returned','post_removal_mutation_verified',
                'fresh_post_removal_delivery','raw_values_exported','real_credentials_used','production_confidentiality_qualified')
            Assert-Fields $o (@('schema','scope','sensitive_property_counts','text_changed_callbacks','cleanup_window_ms')+$observerBools)
            $null=Read-Enum $o 'schema' @('suprachat-synthetic-uia-property-event-witness/v1')
            $null=Read-Enum $o 'scope' @('SYNTHETIC_PROPERTY_CHANGED_VALUES_ONLY')
            $ob=@{};foreach($name in $observerBools){$ob[$name]=Read-Bool $o $name}
            foreach($name in @('legacy_event_source_independently_qualified','text_event_content_read','text_event_content_qualified',
                'raw_values_exported','real_credentials_used','production_confidentiality_qualified')){if($ob[$name]){throw 'UNQUALIFIED_SCOPE_CLAIM'}}
            $null=Read-Count $o 'text_changed_callbacks' 256
            if((Read-Count $o 'cleanup_window_ms' 5000) -ne 750){throw 'CLEANUP_WINDOW_REFUSED'}
            $counts=$o.GetProperty('sensitive_property_counts')
            if($counts.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $counts.GetArrayLength() -ne 8){throw 'PROPERTY_SET_REFUSED'}
            $ids=[Collections.Generic.HashSet[int]]::new();$total=0
            foreach($item in $counts.EnumerateArray()){
                Assert-Fields $item @('property_id','count')
                $id=Read-Count $item 'property_id' 40000
                if($id -notin @(30005,30013,30045,30092,30093,30094,30097,30100) -or !$ids.Add($id)){throw 'PROPERTY_SET_REFUSED'}
                $total+=Read-Count $item 'count' 256
            }
            if($total -gt 256){throw 'CALLBACK_BOUND_REFUSED'}
            $observerDisclosure=$ob.sensitive_value_disclosed;$observerPassed=$ob.passed
            if($ob.passed){
                foreach($name in @('listener_ready','positive_control_observed','fill_control_observed','value_control_observed',
                    'value_fill_observed','removal_returned','post_removal_mutation_verified')){if(!$ob[$name]){throw 'OBSERVER_WITNESS_MISSING'}}
                if($ob.sensitive_value_disclosed -or $ob.callback_unknown -or $ob.fresh_post_removal_delivery){throw 'OBSERVER_PASS_CONTRADICTION'}
            }
        }
        $disclosure=$b.sticky_disclosure_observed -or $observerDisclosure
        if($disclosure -ne ($outcome -ceq 'DISCLOSURE_OBSERVED')){throw 'DISCLOSURE_DOWNGRADE_REFUSED'}
        if($b.passed){
            if($outcome -cne 'OBSERVED_SAFE' -or $failure -cne 'NONE' -or !$observerPassed){throw 'PASS_CONTRADICTION'}
            foreach($name in @('owned_process_exit_confirmed','listener_ready','name_positive_observed','value_positive_observed',
                'final_diagnostic_received')){if(!$b[$name]){throw 'POSITIVE_WITNESS_MISSING'}}
            foreach($name in @('sticky_disclosure_observed','callback_unknown','simulated_callback','sticky_disclosure_negative_passed',
                'negative_control_expected','negative_control_refused')){if($b[$name]){throw 'PASS_CONTRADICTION'}}
        }elseif($outcome -ceq 'OBSERVED_SAFE' -or $failure -ceq 'NONE'){throw 'OUTCOME_CONTRADICTION'}
        if($b.negative_control_refused -and (!$b.negative_control_expected -or $b.passed -or !$b.listener_ready -or
            $b.name_positive_observed -or $b.value_positive_observed -or !$b.public_value_mutation_verified -or
            !$b.final_diagnostic_received -or !$b.owned_process_exit_confirmed -or $disclosure -or $b.callback_unknown)){throw 'NEGATIVE_WITNESS_MISSING'}
        if($b.sticky_disclosure_negative_passed -and (!$b.simulated_callback -or !$b.sticky_disclosure_observed -or
            !$b.final_diagnostic_received -or !$b.owned_process_exit_confirmed -or $b.passed)){throw 'SIMULATION_WITNESS_MISSING'}
        return [ordered]@{schema='suprachat-synthetic-uia-event-validation/v1';document_contract_valid=$true;
            property_qualification_passed=$b.passed;outcome=$outcome;simulated_callback=$b.simulated_callback;
            negative_control_refused=$b.negative_control_refused;production_confidentiality_qualified=$false}
    }finally{$doc.Dispose()}
}
if($SelfTest){
    $sample=[ordered]@{schema='suprachat-synthetic-uia-event-test/v1';passed=$false;failure='EVENT_HANDSHAKE_REFUSED';observer=$null;
        outcome='NOT_VERIFIED';first_party_account_involved=$false;raw_values_exported=$false;owned_process_exit_confirmed=$true;
        listener_ready=$true;name_positive_observed=$true;value_positive_observed=$false;public_value_mutation_verified=$true;
        final_diagnostic_received=$true;sticky_disclosure_observed=$false;callback_unknown=$false;simulated_callback=$false;
        sticky_disclosure_negative_passed=$false;negative_control_expected=$false;negative_control_refused=$false}
    $good=$sample|ConvertTo-Json -Depth 10 -Compress
    if((Test-EventDocument $good).property_qualification_passed){throw 'UNKNOWN_UPGRADED'}
    $negative=@(
        $good.Replace('"passed":false','"passed":true'),
        $good.Replace('"passed":false','"passed":"false"'),
        $good.Replace('"passed":false','"passed":false,"passed":false'),
        $good.Replace('"observer":null','"raw_callback":"synthetic","observer":null'),
        $good.Replace('"outcome":"NOT_VERIFIED"','"outcome":"OBSERVED_SAFE"'),
        $good.Replace('"sticky_disclosure_observed":false','"sticky_disclosure_observed":true'),
        $good.Replace('"negative_control_refused":false','"negative_control_refused":true'),
        $good.Replace('"simulated_callback":false','"simulated_callback":null'),
        $good.Replace('"first_party_account_involved":false','"first_party_account_involved":true'),
        $good.Replace('"raw_values_exported":false','"raw_values_exported":true'))
    foreach($bad in $negative){$refused=$false;try{$null=Test-EventDocument $bad}catch{$refused=$true};if(!$refused){throw 'NEGATIVE_ACCEPTED'}}
    $sample.name_positive_observed=$false;$sample.negative_control_expected=$true;$sample.negative_control_refused=$true
    $null=Test-EventDocument ($sample|ConvertTo-Json -Depth 10 -Compress)
    $sample.negative_control_expected=$false;$sample.negative_control_refused=$false;$sample.simulated_callback=$true
    $sample.sticky_disclosure_observed=$true;$sample.sticky_disclosure_negative_passed=$true;$sample.outcome='DISCLOSURE_OBSERVED'
    $result=Test-EventDocument ($sample|ConvertTo-Json -Depth 10 -Compress)
    if($result.property_qualification_passed -or $result.outcome -cne 'DISCLOSURE_OBSERVED'){throw 'DISCLOSURE_LOST'}
    $observer=[ordered]@{schema='suprachat-synthetic-uia-property-event-witness/v1';scope='SYNTHETIC_PROPERTY_CHANGED_VALUES_ONLY';
        sensitive_property_counts=@(@(30005,30013,30045,30092,30093,30094,30097,30100)|ForEach-Object{@{property_id=$_;count=0}});
        text_changed_callbacks=0;cleanup_window_ms=750}
    foreach($name in @('passed','listener_ready','positive_control_observed','fill_control_observed','value_control_observed',
        'value_fill_observed','removal_returned','post_removal_mutation_verified')){$observer[$name]=$true}
    foreach($name in @('legacy_event_source_independently_qualified','sensitive_value_disclosed','callback_unknown','text_event_content_read',
        'text_event_content_qualified','fresh_post_removal_delivery','raw_values_exported','real_credentials_used','production_confidentiality_qualified')){$observer[$name]=$false}
    $sample.passed=$true;$sample.failure='NONE';$sample.outcome='OBSERVED_SAFE';$sample.observer=$observer
    $sample.name_positive_observed=$true;$sample.value_positive_observed=$true;$sample.simulated_callback=$false
    $sample.sticky_disclosure_observed=$false;$sample.sticky_disclosure_negative_passed=$false
    $positive=$sample|ConvertTo-Json -Depth 10 -Compress
    if(!(Test-EventDocument $positive).property_qualification_passed){throw 'BOUNDED_POSITIVE_REFUSED'}
    $nestedBad=@(
        $positive.Replace('"property_id":30013','"property_id":30005'),
        $positive.Replace('"property_id":30013','"property_id":31337'),
        $positive.Replace('"count":0','"count":-1'),
        $positive.Replace('"cleanup_window_ms":750','"cleanup_window_ms":0'),
        $positive.Replace('"removal_returned":true,',''),
        $positive.Replace('"real_credentials_used":false','"real_credentials_used":true'),
        $positive.Replace('"callback_unknown":false','"callback_unknown":true'),
        $positive.Replace('suprachat-synthetic-uia-property-event-witness/v1','unknown-observer/v1'))
    foreach($bad in $nestedBad){if($bad -ceq $positive){throw 'NEGATIVE_MUTATION_NOOP'};$refused=$false;try{$null=Test-EventDocument $bad}catch{$refused=$true};if(!$refused){throw 'NESTED_NEGATIVE_ACCEPTED'}}
    @{schema='suprachat-uia-event-validator-selftest/v1';passed=$true;cases=22;live_events=$false}|ConvertTo-Json
    return
}
try{
    if(!$ReceiptPath){throw 'PATH_REQUIRED'}
    Test-EventDocument ([IO.File]::ReadAllText([IO.Path]::GetFullPath($ReceiptPath)))|ConvertTo-Json
}catch{throw 'UIA_EVENT_RECEIPT_REFUSED'}
