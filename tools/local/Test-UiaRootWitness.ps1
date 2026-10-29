param([string]$ReceiptPath,[switch]$SelfTest)
$ErrorActionPreference='Stop'
function Check($json){
 if([string]::IsNullOrWhiteSpace($json) -or $json.Length -gt 16384){throw 'INPUT_BOUND'}
 $d=[Text.Json.JsonDocument]::Parse($json)
 try{
  $r=$d.RootElement
  if($r.ValueKind -ne [Text.Json.JsonValueKind]::Object){throw 'OBJECT_REQUIRED'}
  $fields=@('schema','process_alive','os_window_valid','os_pid_matches','uia_pid_matches','wm_null_responded','wm_null_error','wm_null_elapsed_ms','uia_root_id_matches','probe_completed','failure_stage','failure_hresult','known_controls','scope','raw_values_exported')
  $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  foreach($p in $r.EnumerateObject()){if($p.Name -cnotin $fields -or !$seen.Add($p.Name)){throw 'FIELD_REFUSED'}}
  if($seen.Count -ne $fields.Count){throw 'FIELD_MISSING'}
  if($r.GetProperty('schema').GetString() -cne 'suprachat-fresh-uia-root-witness/v2' -or
   $r.GetProperty('scope').GetString() -cne 'FIXED_NATIVE_IDS_ONLY'){throw 'SCHEMA_REFUSED'}
  $st=$r.GetProperty('failure_stage').GetString()
  $stages=@('ARGUMENTS','PROCESS','OS_WINDOW','OS_RESPONSIVENESS','FROM_HANDLE','UIA_PID','UIA_ROOT_ID','CONTROL_QUERY','NONE')
  if($st -cnotin $stages){throw 'STAGE_REFUSED'}
  function B($name,$nullable){
   $v=$r.GetProperty($name)
   if($nullable -and $v.ValueKind -eq [Text.Json.JsonValueKind]::Null){return $null}
   if($v.ValueKind -notin @([Text.Json.JsonValueKind]::True,[Text.Json.JsonValueKind]::False)){throw 'BOOLEAN_REFUSED'}
   return $v.GetBoolean()
  }
  function N($name,$nullable,$min,$max){
   $v=$r.GetProperty($name)
   if($nullable -and $v.ValueKind -eq [Text.Json.JsonValueKind]::Null){return $null}
   [long]$n=0
   if($v.ValueKind -ne [Text.Json.JsonValueKind]::Number -or !$v.TryGetInt64([ref]$n) -or $n -lt $min -or $n -gt $max){throw 'INTEGER_REFUSED'}
   return $n
  }
  if(B 'raw_values_exported' $false){throw 'EXPORT_REFUSED'}
  $alive=B 'process_alive' $false
  $win=B 'os_window_valid' $true;$owner=B 'os_pid_matches' $true
  $uiaPid=B 'uia_pid_matches' $true;$id=B 'uia_root_id_matches' $true
  $wm=B 'wm_null_responded' $true;$error=N 'wm_null_error' $true 0 65535
  $elapsed=N 'wm_null_elapsed_ms' $true 0 10000
  $hr=N 'failure_hresult' $true -2147483648 2147483647
  $done=B 'probe_completed' $false
  $rank=[array]::IndexOf($stages,$st);if($st -eq 'NONE'){$rank=99}
  if($done -ne ($st -eq 'NONE') -or ($done -and $null -ne $hr) -or (!$done -and $null -eq $hr)){throw 'COMPLETION_REFUSED'}
  if($rank -lt 2 -and ($win -ne $null -or $owner -ne $null)){throw 'PREMATURE_OS'}
  if($rank -ge 2 -and !$alive){throw 'PROCESS_UNVERIFIED'}
  if($rank -ge 3 -and ($win -ne $true -or $owner -ne $true)){throw 'OWNER_UNVERIFIED'}
  if($rank -lt 4 -and ($null -ne $wm -or $null -ne $elapsed -or $null -ne $error)){throw 'PREMATURE_WM'}
  if($rank -ge 4 -and ($null -eq $wm -or $null -eq $elapsed -or ($wm -and $null -ne $error) -or (!$wm -and $null -eq $error))){throw 'WM_UNVERIFIED'}
  if($rank -le 4 -and ($null -ne $uiaPid -or $null -ne $id)){throw 'UNOBSERVED_UIA'}
  if($rank -ge 6 -and $uiaPid -ne $true){throw 'UIA_PID_UNVERIFIED'}
  if($rank -lt 6 -and $null -ne $id){throw 'EARLY_UIA_ID'}
  if($rank -ge 7 -and $id -ne $true){throw 'UIA_ROOT_UNVERIFIED'}
  $controls=$r.GetProperty('known_controls')
  if($rank -lt 7 -and $controls.ValueKind -ne [Text.Json.JsonValueKind]::Null){throw 'EARLY_CONTROLS'}
  if($rank -ge 7){
   if($controls.ValueKind -ne [Text.Json.JsonValueKind]::Object){throw 'CONTROLS_REQUIRED'}
   $main=@('RootTabs','Tab.ChatGPT','Tab.AgentLab','Tab.AutomationAgents','Nav.Home','Auth.SignIn','AgentLab.Prompt','Auth.Status')
   $synthetic=@('Synthetic.TabProbe.Toolbar','Synthetic.TabProbe.SecondTab','Synthetic.TabProbe.SecondButton')
   $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
   foreach($p in $controls.EnumerateObject()){
    if($p.Name -cnotin ($main+$synthetic) -or !$seen.Add($p.Name)){throw 'CONTROL_REFUSED'}
    if($p.Value.ValueKind -notin @([Text.Json.JsonValueKind]::True,[Text.Json.JsonValueKind]::False,[Text.Json.JsonValueKind]::Null)){throw 'CONTROL_TYPE'}
    if($done -and $p.Value.ValueKind -eq [Text.Json.JsonValueKind]::Null){throw 'INCOMPLETE_SUCCESS'}
   }
   if($seen.Count -notin @(3,8) -or ($seen.Count -eq 3 -and @($seen|Where-Object {$_ -cnotin $synthetic}).Count -gt 0) -or
    ($seen.Count -eq 8 -and @($seen|Where-Object {$_ -cnotin $main}).Count -gt 0)){throw 'CONTROL_SET'}
  }
  $result=if($done){'OBSERVED_ROOT'}elseif($st -eq 'FROM_HANDLE'){'UIA_ROOT_UNAVAILABLE'}else{'NOT_VERIFIED'}
  return [ordered]@{schema='suprachat-uia-root-receipt-validation/v1';valid=$true;outcome=$result;stage=$st;raw_values_exported=$false}
 }finally{$d.Dispose()}
}
function SelfTest {
 $ok=[ordered]@{schema='suprachat-fresh-uia-root-witness/v2';process_alive=$true;os_window_valid=$true;os_pid_matches=$true;
 uia_pid_matches=$true;wm_null_responded=$true;wm_null_error=$null;wm_null_elapsed_ms=5;uia_root_id_matches=$true;
 probe_completed=$true;failure_stage='NONE';failure_hresult=$null;
 known_controls=[ordered]@{'Synthetic.TabProbe.Toolbar'=$true;'Synthetic.TabProbe.SecondTab'=$true;'Synthetic.TabProbe.SecondButton'=$true};
 scope='FIXED_NATIVE_IDS_ONLY';raw_values_exported=$false}
 $count=0
 function Accept($x,$expected){$v=Check (($x | ConvertTo-Json -Depth 8 -Compress));if($v.outcome -cne $expected){throw 'ACCEPT_FAILED'};$script:count++}
 function Reject($x){try{$null=Check (($x | ConvertTo-Json -Depth 8 -Compress));throw 'UNEXPECTED_PASS'}catch{if($_.Exception.Message -ceq 'UNEXPECTED_PASS'){throw}};$script:count++}
 function Clone($x){return (ConvertFrom-Json -AsHashtable (($x | ConvertTo-Json -Depth 8 -Compress)))}
 Accept $ok 'OBSERVED_ROOT'
 $timeout=Clone $ok;$timeout.failure_stage='FROM_HANDLE';$timeout.probe_completed=$false;$timeout.failure_hresult=-2147023436;$timeout.uia_pid_matches=$null;$timeout.uia_root_id_matches=$null;$timeout.known_controls=$null
 Accept $timeout 'UIA_ROOT_UNAVAILABLE'
 $timeout.wm_null_responded=$false;$timeout.wm_null_error=1460;Accept $timeout 'UIA_ROOT_UNAVAILABLE'
 foreach($change in @(@('raw_values_exported',$true),@('probe_completed',$false),@('schema','wrong'),@('failure_stage','MISREAD'),@('uia_pid_matches',$false),@('uia_root_id_matches',$false),@('wm_null_error',1460),@('wm_null_elapsed_ms',-1),@('os_pid_matches',$false),@('process_alive',$false),@('failure_hresult',1))){
  $b=Clone $ok;$b[$change[0]]=$change[1];Reject $b
 }
 $b=Clone $ok;$b.known_controls=@{'Other.Id'=$true};Reject $b
 $b=Clone $ok;$b.known_controls['Synthetic.TabProbe.SecondTab']=$null;Reject $b
 $b=Clone $timeout;$b.uia_pid_matches=$false;Reject $b
 $b=Clone $timeout;$b.wm_null_responded=$null;Reject $b
 $b=Clone $timeout;$b.wm_null_error=$null;Reject $b
 $b=Clone $ok;$b.extra='sensitive';Reject $b
 $json=(($ok | ConvertTo-Json -Depth 8 -Compress)).Replace('"raw_values_exported":false','"raw_values_exported":false,"raw_values_exported":false')
 try{$null=Check $json;throw 'UNEXPECTED_PASS'}catch{if($_.Exception.Message -ceq 'UNEXPECTED_PASS'){throw}};$count++
 [ordered]@{schema='suprachat-uia-root-validator-selftest/v1';passed=$true;cases=$count;provider_contact=$false}|ConvertTo-Json -Compress
}
if($SelfTest){SelfTest}
elseif($ReceiptPath){if((Get-Item -LiteralPath $ReceiptPath).Length -gt 16384){throw 'INPUT_BOUND'};Check ([IO.File]::ReadAllText([IO.Path]::GetFullPath($ReceiptPath)))|ConvertTo-Json -Compress}
else{throw 'RECEIPT_OR_SELFTEST_REQUIRED'}
