#Requires -Version 5.1
<#
Same-process root acquisition differential for public synthetic Windows RDTE.
Read-only structural fixed IDs. No names, value/text patterns, raw content,
screenshots, cookies, network calls, credential fields or control invocation.
#>
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$ProcessId,
 [ValidateRange(1,6)][int]$Samples=3
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p=Get-Process -Id $ProcessId -ErrorAction Stop
if($p.ProcessName -ne 'SupraChat'){throw 'REFUSED_NON_SUPRACHAT_PROCESS'}
$firstStart=$p.StartTime.ToUniversalTime()
$records=New-Object 'System.Collections.Generic.List[object]'
$clock=[Diagnostics.Stopwatch]::StartNew()
for($i=1;$i -le $Samples;$i++){
  $r=[ordered]@{
    sample=$i;elapsed_ms=[int]$clock.ElapsedMilliseconds
    handle_root_valid=$false;process_search_root_valid=$false
    handle_target=$false;process_search_target=$false
    handle_findall_target=$false;handle_descendants=$null
    root_search_ms=$null;handle_findfirst_ms=$null;process_findfirst_ms=$null
    error_class=$null
  }
  try {
    $p=Get-Process -Id $ProcessId -ErrorAction Stop
    if($p.ProcessName -ne 'SupraChat' -or $p.StartTime.ToUniversalTime() -ne $firstStart){throw 'PROCESS_IDENTITY_CHANGED'}
    $p.Refresh();if($p.HasExited){throw 'PROCESS_EXITED'}
    $procCondition=New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId)
    $idCondition=New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::AutomationIdProperty,'Tab.Sessions')
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $searchRoot=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
      [System.Windows.Automation.TreeScope]::Descendants,$procCondition)
    $r.root_search_ms=[int]$timer.ElapsedMilliseconds
    $window=$null
    if($p.MainWindowHandle -ne [IntPtr]::Zero){
      $window=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    }
    if($null -ne $searchRoot){
      $r.process_search_root_valid=($searchRoot.Current.ProcessId -eq $ProcessId -and
        $searchRoot.Current.AutomationId -eq 'SupraChat.MainWindow')
      if($r.process_search_root_valid){
        $timer.Restart()
        $target=$searchRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$idCondition)
        $r.process_findfirst_ms=[int]$timer.ElapsedMilliseconds
        $r.process_search_target=($null -ne $target)
      }
    }
    if($null -ne $window){
      $r.handle_root_valid=($window.Current.ProcessId -eq $ProcessId -and
         $window.Current.AutomationId -eq 'SupraChat.MainWindow')
      if($r.handle_root_valid){
        $timer.Restart()
        $target=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$idCondition)
        $r.handle_findfirst_ms=[int]$timer.ElapsedMilliseconds
        $r.handle_target=($null -ne $target)
        $nodes=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
          [System.Windows.Automation.Condition]::TrueCondition)
        $r.handle_descendants=$nodes.Count
        foreach($node in $nodes){
          if($node.Current.AutomationId -eq 'Tab.Sessions'){$r.handle_findall_target=$true}
        }
      }
    }
  } catch {
    $r.error_class=$_.Exception.GetType().Name
  }
  $records.Add([pscustomobject]$r)
  if($i -lt $Samples){Start-Sleep -Milliseconds 600}
}
[pscustomobject]@{
 schema='suprachat-uia-root-selection/v1'
 evidence_class='PUBLIC_SYNTHETIC_NATIVE_UIA'
 readonly=$true
 captured_sensitive_content=$false
 samples=$records.ToArray()
} | ConvertTo-Json -Compress -Depth 6
