# Windows UI Automation end-to-end causal test. Public synthetic localhost data only.
param([string]$GuiExe='SupraChat/bin/Release/net10.0/SupraChat.exe')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Require([bool]$yes,[string]$why) { if(-not $yes){throw $why} }
function Find-Control($root,[string]$id,[int]$wait=8) {
  $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
  $until=(Get-Date).AddSeconds($wait)
  do {
    try {
      $el=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
      if($el){return $el}
    } catch {
      if($_.Exception.Message -notmatch '0x8000FFFF|E_UNEXPECTED'){throw}
    }
    Start-Sleep -Milliseconds 200
  }while((Get-Date)-lt $until)
  throw "EXPECTED_UIA_PEER_MISSING:$id"
}
function Ui-Text($el) {
  $name=$el.Current.Name
  if([string]::IsNullOrWhiteSpace($name)){throw "UIA_STATUS_NAME_UNAVAILABLE:$($el.Current.AutomationId)"}
  return $name
}
function Invoke-Ui($el){$el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Until([scriptblock]$f,[string]$why,[int]$limit=8) {
  $end=(Get-Date).AddSeconds($limit)
  do {
    if(& $f){return}
    Start-Sleep -Milliseconds 120
  }while((Get-Date)-lt $end)
  throw "UIA_TIMEOUT:$why"
}
$oldToken=[Environment]::GetEnvironmentVariable('SUPRACHAT_LEDGER_TOKEN')
$token=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
$env:SUPRACHAT_LEDGER_TOKEN=$token
$listener=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
$listener.Start()
$port=([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$nodeOut=Join-Path $env:RUNNER_TEMP 'w16-fixture-out.txt'
$nodeErr=Join-Path $env:RUNNER_TEMP 'w16-fixture-err.txt'
$fixture=$null
$app=$null
$stages=[System.Collections.Generic.List[string]]::new()
try {
  $fixture=Start-Process -FilePath node -ArgumentList @('tools/qualify-w16-uia-fixture.mjs',"$port") -PassThru -RedirectStandardOutput $nodeOut -RedirectStandardError $nodeErr
  Until {
    if($fixture.HasExited){throw 'SYNTHETIC_FIXTURE_PREMATURE_EXIT'}
    if(-not(Test-Path $nodeOut)){return $false}
    $lines=@(Get-Content $nodeOut)
    if($lines.Count -ne 1){return $false}
    try {
      $r=$lines[0]|ConvertFrom-Json
      return ($r.ready -eq $true -and $r.port -eq $port -and $r.synthetic -eq $true)
    }catch{return $false}
  } 'fixture-ready' 12
  $stages.Add('bounded-two-session-synthetic-loopback-fixture')
  $headers=@{Authorization="Bearer $token"}
  function Counts {Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$port/__fixture/counts" -Headers $headers}
  Require ((Counts).inventory_gets -eq 0) 'EXPECTED_ZERO_INITIAL_GETS'
  $exe=(Resolve-Path -LiteralPath $GuiExe).Path
  $app=Start-Process -FilePath $exe -PassThru
  $root=$null
  $cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
  Until {
    $root=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)
    return $null -ne $root
  } 'real-native-window-root' 20
  Require ($root.Current.AutomationId -eq 'SupraChat.MainWindow') 'WRONG_NATIVE_UIA_ROOT'
  (Find-Control $root 'Tab.Sessions').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $portField=Find-Control $root 'Sessions.SharedHostPort'
  $read=Find-Control $root 'Sessions.SharedHostRead'
  $sessionList=Find-Control $root 'Sessions.SharedHostSelection'
  $start=Find-Control $root 'Sessions.AutoFollowStart'
  $stop=Find-Control $root 'Sessions.AutoFollowStop'
  $status=Find-Control $root 'Sessions.AutoFollowStatus'
  foreach($control in @($read,$start,$stop,$status)){
    Require (-not [string]::IsNullOrWhiteSpace($control.Current.Name)) "UNNAMED_UIA_CONTROL:$($control.Current.AutomationId)"
  }
  $stages.Add('real-uia-session-controls-with-accessible-names')
  Invoke-Ui $start
  Until {(Ui-Text $status) -match 'Start refused'} 'unselected-start-refusal'
  Require ((Counts).inventory_gets -eq 0) 'UNSELECTED_START_DID_NETWORK_READ'
  $stages.Add('unselected-start-refused-zero-get')
  $portField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("$port")
  Invoke-Ui $read
  Until {(Counts).inventory_gets -eq 1} 'manual-inventory-get'
  $itemCond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
  $items=$null
  Until {
    $items=$sessionList.FindAll([System.Windows.Automation.TreeScope]::Descendants,$itemCond)
    return ($items.Count -eq 2)
  } 'two-accessible-list-items'
  Require ($items[0].Current.Name -match 'UIA Synthetic A') 'WRONG_FIRST_SESSION_LABEL'
  Require ($items[1].Current.Name -match 'UIA Synthetic B') 'WRONG_SECOND_SESSION_LABEL'
  $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $follow=Find-Control $root 'Sessions.SharedHostFollowStatus'
  Until {(Ui-Text $follow) -match 'Host session 123e4567'} 'explicit-session-a-selected'
  $stages.Add('manual-get-two-session-semantic-selection')
  Invoke-Ui $start
  Until {(Counts).inventory_gets -eq 2} 'first-auto-get' 9
  Until {(Ui-Text $status) -match 'Read-only follow active'} 'first-read-delivered' 9
  $stages.Add('native-start-caused-single-observed-get')
  $stop.SetFocus()
  Require ($stop.Current.HasKeyboardFocus) 'STOP_UIA_KEYBOARD_FOCUS_FAILED'
  Invoke-Ui $stop
  Until {(Ui-Text $status) -match 'Automatic observation OFF'} 'native-stop'
  Require ((Counts).inventory_gets -eq 2) 'EXTRA_GET_BEFORE_STOP'
  Start-Sleep -Milliseconds 3550
  $counts=Counts
  Require ($counts.inventory_gets -eq 2) 'POST_STOP_NETWORK_GET'
  Require ($counts.unsafe -eq 0 -and $counts.refused -eq 0) 'ILLEGAL_HTTP_METHOD_OR_BEARER'
  Require ((Ui-Text $status) -match 'Automatic observation OFF') 'LATE_CALLBACK_REPLACED_STOP_STATUS'
  $selected=$items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
  Require ($selected) 'STOP_LOST_SESSION_SELECTION'
  $stages.Add('stop-held-focus-session-and-blocked-late-read-and-status')
  Invoke-Ui $start
  Until {(Counts).inventory_gets -eq 3} 'second-explicit-start' 9
  Until {(Ui-Text $status) -match 'Read-only follow active'} 'second-read-delivered' 9
  Invoke-Ui $stop
  Until {(Ui-Text $status) -match 'Automatic observation OFF'} 'second-stop'
  Start-Sleep -Milliseconds 3550
  Require ((Counts).inventory_gets -eq 3) 'RESTART_OR_STOP_CAUSED_EXTRA_GET'
  $stages.Add('clean-second-explicit-start-stop-single-flight')
  Write-Host ([ordered]@{schema='suprachat-w16-native-uia-causal/v1';status='PASS';stages=@($stages);inventory_gets=3;unsafe_http_methods=0;synthetic_sessions=2;native_uia=$true;focus_tested=$true;spoken_screenreader_output_proven=$false;real_chatgpt_product_traffic=$false;provider_turns=0;terminal_operations=0;credentials_exported=$false}|ConvertTo-Json -Compress -Depth 5)
} finally {
  if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue}
  if($fixture -and -not $fixture.HasExited){Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue}
  [Environment]::SetEnvironmentVariable('SUPRACHAT_LEDGER_TOKEN',$oldToken)
}
