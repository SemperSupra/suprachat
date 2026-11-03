#Requires -Version 5.1
<#
Source-safe native UIA comparison. No values, names, credentials, input,
control actions, clipboard, network calls or provider mutation are collected.
Only a freshly launched synthetic SupraChat process is admitted.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$ProcessId,
  [ValidateRange(1,10)][int]$Samples=6,
  [ValidateRange(250,3000)][int]$IntervalMs=1000
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p=Get-Process -Id $ProcessId -ErrorAction Stop
if($p.ProcessName -ne 'SupraChat') { throw 'REFUSED_NON_SYNTHETIC_PROCESS' }
$initialStart=$p.StartTime.ToUniversalTime()
$clock=[Diagnostics.Stopwatch]::StartNew()
$records=New-Object 'System.Collections.Generic.List[object]'
for($i=1;$i -le $Samples;$i++){
  $r=[ordered]@{sample=$i; elapsed_ms=[int]$clock.ElapsedMilliseconds;
    root_present=$false; root_id_valid=$false; root_process_valid=$false;
    findall_count=$null; findall_target=$null; findall_ms=$null;
    walker_count=$null; walker_target=$null; walker_ms=$null;
    error_class=$null}
  try {
    $p=Get-Process -Id $ProcessId -ErrorAction Stop
    if($p.ProcessName -ne 'SupraChat' -or $p.StartTime.ToUniversalTime() -ne $initialStart){
      throw 'UNEXPECTED_PROCESS_IDENTITY'
    }
    $p.Refresh()
    if($p.HasExited){throw 'PROCESS_EXITED'}
    if($p.MainWindowHandle -ne [IntPtr]::Zero){
      $root=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
      if($null -ne $root){
        $r.root_present=$true
        $r.root_id_valid=($root.Current.AutomationId -eq 'SupraChat.MainWindow')
        $r.root_process_valid=($root.Current.ProcessId -eq $ProcessId)
        if($r.root_id_valid -and $r.root_process_valid){
          $timer=[Diagnostics.Stopwatch]::StartNew()
          $all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                            [System.Windows.Automation.Condition]::TrueCondition)
          $r.findall_ms=[int]$timer.ElapsedMilliseconds
          $r.findall_count=$all.Count
          $found=0
          foreach($item in $all){if($item.Current.AutomationId -eq 'Tab.Sessions'){$found++}}
          $r.findall_target=$found
          $timer.Restart()
          $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
          $q=New-Object System.Collections.Queue
          $q.Enqueue($root)
          $count=0;$target=0
          while($q.Count -gt 0 -and $count -lt 3000){
            $parent=[System.Windows.Automation.AutomationElement]$q.Dequeue()
            $child=$walker.GetFirstChild($parent)
            while($null -ne $child -and $count -lt 3000){
              $count++
              if($child.Current.AutomationId -eq 'Tab.Sessions'){$target++}
              $q.Enqueue($child)
              $child=$walker.GetNextSibling($child)
            }
          }
          $r.walker_ms=[int]$timer.ElapsedMilliseconds
          $r.walker_count=$count
          $r.walker_target=$target
        }
      }
    }
  } catch {
    $r.error_class=$_.Exception.GetType().Name
  }
  $records.Add([pscustomobject]$r)
  if($i -lt $Samples){Start-Sleep -Milliseconds $IntervalMs}
}
$valid=@($records | Where-Object {$_.root_id_valid -and $_.root_process_valid})
if($valid.Count -eq 0){throw 'NO_VALID_NATIVE_UIA_ROOT_OBSERVED'}
[pscustomobject]@{
 schema='suprachat-uia-dual-view/v1'
 evidence_class='PUBLIC_SYNTHETIC_NATIVE_UIA'
 readonly=$true
 sensitive_content_captured=$false
 sample_count=$records.Count
 samples=$records.ToArray()
} | ConvertTo-Json -Depth 5 -Compress
