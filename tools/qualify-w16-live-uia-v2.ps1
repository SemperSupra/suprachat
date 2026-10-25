# Disposable public Windows UI Automation proof. The workflow owns the synthetic host.
param([Parameter(Mandatory)][int]$Port,[string]$GuiExe='SupraChat/bin/Release/net10.0/SupraChat.exe')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Require([bool]$ok,[string]$reason){if(-not $ok){throw $reason}}
function Wait-For([scriptblock]$probe,[string]$name,[int]$maxSeconds=9){
  $end=(Get-Date).AddSeconds($maxSeconds)
  do {
    $result=& $probe
    if($result){return $result}
    Start-Sleep -Milliseconds 150
  }while((Get-Date)-lt $end)
  throw "UIA_TIMEOUT:$name"
}
function Find-Peer($root,[string]$name){
  $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$name)
  return Wait-For {
    try {$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)}
    catch {if($_.Exception.Message -match '0x8000FFFF|E_UNEXPECTED'){$null}else{throw}}
  } "peer:$name"
}
function Push($peer){$peer.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Text($peer){return $peer.Current.Name}
function Counts {
  Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$Port/__fixture/counts" -Headers @{Authorization="Bearer $env:SUPRACHAT_LEDGER_TOKEN"}
}
$app=$null
$stages=[System.Collections.Generic.List[string]]::new()
try {
  Require ((Counts).inventory_gets -eq 0) 'FIRST_GET_COUNT_NOT_ZERO'
  $app=Start-Process -FilePath (Resolve-Path -LiteralPath $GuiExe).Path -PassThru
  $cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
  $window=Wait-For {
    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)
  } 'real-main-window' 20
  Require ($window.Current.AutomationId -eq 'SupraChat.MainWindow') 'WRONG_UIA_WINDOW'
  (Find-Peer $window 'Tab.Sessions').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $portPeer=Find-Peer $window 'Sessions.SharedHostPort'
  $read=Find-Peer $window 'Sessions.SharedHostRead'
  $list=Find-Peer $window 'Sessions.SharedHostSelection'
  $start=Find-Peer $window 'Sessions.AutoFollowStart'
  $stop=Find-Peer $window 'Sessions.AutoFollowStop'
  $status=Find-Peer $window 'Sessions.AutoFollowStatus'
  foreach($peer in @($read,$start,$stop,$status)){
    Require (-not [string]::IsNullOrWhiteSpace((Text $peer))) "MISSING_ACCESSIBLE_NAME:$($peer.Current.AutomationId)"
  }
  Push $start
  $null=Wait-For {if((Text $status) -match 'Start refused'){return $true}} 'refuse-without-selection'
  Require ((Counts).inventory_gets -eq 0) 'UNSELECTED_GUI_READ'
  $stages.Add('native-start-refused-without-selected-host')

  $portPeer.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("$Port")
  Push $read
  $null=Wait-For {if((Counts).inventory_gets -eq 1){return $true}} 'explicit-host-inventory'
  $itemCond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
  $items=@(Wait-For {
    $collection=$list.FindAll([System.Windows.Automation.TreeScope]::Descendants,$itemCond)
    if($collection.Count -eq 2){return $collection}
  } 'two-session-list')
  Require ($items.Count -eq 2) 'SESSION_LIST_ITEMS_MISSING'
  Require ($items[0].Current.Name -match 'UIA Synthetic A') 'FIRST_SESSION_LABEL_MISSING'
  Require ($items[1].Current.Name -match 'UIA Synthetic B') 'SECOND_SESSION_LABEL_MISSING'
  $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $follow=Find-Peer $window 'Sessions.SharedHostFollowStatus'
  $null=Wait-For {if((Text $follow) -match 'Host session 123e4567'){return $true}} 'explicit-selection'
  $stages.Add('real-ui-accessible-two-session-selection')

  Push $start
  $null=Wait-For {if((Counts).inventory_gets -eq 2){return $true}} 'first-automatic-read'
  $null=Wait-For {if((Text $status) -match 'Read-only follow active'){return $true}} 'read-delivered'
  $stages.Add('start-invoked-caused-one-read')
  $stop.SetFocus()
  Require ($stop.Current.HasKeyboardFocus) 'STOP_CONTROL_NOT_KEYBOARD_FOCUSABLE'
  Push $stop
  $null=Wait-For {if((Text $status) -match 'Automatic observation OFF'){return $true}} 'stop-signaled'
  Require ((Counts).inventory_gets -eq 2) 'UNEXPECTED_GET_DURING_STOP'
  Start-Sleep -Milliseconds 3550
  $observed=Counts
  Require ($observed.inventory_gets -eq 2) 'POST_STOP_NETWORK_GET'
  Require ($observed.unsafe -eq 0 -and $observed.refused -eq 0) 'NETWORK_SCOPE_VIOLATION'
  Require ((Text $status) -match 'Automatic observation OFF') 'LATE_UI_STATUS_MUTATION'
  $stages.Add('stop-retains-focus-and-blocks-next-get-or-stale-ui')

  Push $start
  $null=Wait-For {if((Counts).inventory_gets -eq 3){return $true}} 'explicit-restart-read'
  $null=Wait-For {if((Text $status) -match 'Read-only follow active'){return $true}} 'restart-delivered'
  Push $stop
  $null=Wait-For {if((Text $status) -match 'Automatic observation OFF'){return $true}} 'restart-stop'
  Start-Sleep -Milliseconds 3550
  Require ((Counts).inventory_gets -eq 3) 'SECOND_STOP_LEAKED_GET'
  $stages.Add('second-explicit-run-finite-and-single-flight')
  Write-Host ([ordered]@{
    schema='suprachat-w16-windows-live-uia/v1';status='PASS';stages=@($stages);
    observed_gets=3;synthetic_session_count=2;actual_native_uia=$true;
    status_and_focus_tested=$true;screen_reader_speech_proven=$false;
    signed_in_chatgpt_requests=0;terminal_actions=0;provider_turns=0
  }|ConvertTo-Json -Compress -Depth 4)
} finally {
  if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue}
}
