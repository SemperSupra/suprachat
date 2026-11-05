#Requires -Version 5.1
<#
Read-only actual Windows UIA apartment comparison.
No Value/TextPattern, UI names, browser content, credentials, clicks, DOM,
clipboard, network, installation state or application mutation.
#>
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$ProcessId,
 [ValidateRange(1,4)][int]$Samples=3
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p=Get-Process -Id $ProcessId -ErrorAction Stop
if($p.ProcessName -ne 'SupraChat'){throw 'WRONG_PROCESS_REFUSED'}
$start=$p.StartTime.ToUniversalTime()
$clock=[Diagnostics.Stopwatch]::StartNew()
$records=New-Object 'System.Collections.Generic.List[object]'
for($i=1;$i -le $Samples;$i++){
  $r=[ordered]@{
    sample=$i; elapsed_ms=[int]$clock.ElapsedMilliseconds
    apartment=[Threading.Thread]::CurrentThread.GetApartmentState().ToString()
    process_identity_valid=$false; root_valid=$false
    findall_count=$null; findall_target=$false; findall_ms=$null
    walker_count=$null; walker_target=$false; walker_ms=$null
    error_class=$null
  }
  try {
    $p=Get-Process -Id $ProcessId -ErrorAction Stop
    if($p.ProcessName -ne 'SupraChat' -or $p.StartTime.ToUniversalTime() -ne $start){throw 'PROCESS_IDENTITY_CHANGED'}
    $r.process_identity_valid=$true
    $p.Refresh()
    if($p.HasExited){throw 'PROCESS_EXITED'}
    if($p.MainWindowHandle -ne [IntPtr]::Zero) {
      $root=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
      if($null -ne $root) {
        $r.root_valid=($root.Current.ProcessId -eq $ProcessId -and
                      $root.Current.AutomationId -eq 'SupraChat.MainWindow')
        if($r.root_valid) {
          $timer=[Diagnostics.Stopwatch]::StartNew()
          $found=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
             [System.Windows.Automation.Condition]::TrueCondition)
          $r.findall_ms=[int]$timer.ElapsedMilliseconds
          $r.findall_count=$found.Count
          foreach($node in $found) {
            if($node.Current.AutomationId -eq 'Tab.Sessions'){$r.findall_target=$true}
          }
          $timer.Restart()
          $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
          $queue=New-Object System.Collections.Queue
          $queue.Enqueue($root)
          $seen=0
          while($queue.Count -gt 0 -and $seen -lt 1000){
            $parent=[System.Windows.Automation.AutomationElement]$queue.Dequeue()
            $child=$walker.GetFirstChild($parent)
            while($null -ne $child -and $seen -lt 1000){
              $seen++
              if($child.Current.AutomationId -eq 'Tab.Sessions'){$r.walker_target=$true}
              $queue.Enqueue($child)
              $child=$walker.GetNextSibling($child)
            }
          }
          $r.walker_ms=[int]$timer.ElapsedMilliseconds
          $r.walker_count=$seen
        }
      }
    }
  } catch {
    # Never retain exception message or stack; class alone discriminates.
    $r.error_class=$_.Exception.GetType().Name
  }
  $records.Add([pscustomobject]$r)
  if($i -lt $Samples){Start-Sleep -Milliseconds 400}
}
[pscustomobject]@{
  schema='suprachat-uia-apartment-differential/v1'
  evidence_class='PUBLIC_SYNTHETIC_NATIVE_UIA'
  synthetic_fixture_only=$true
  samples=$records.ToArray()
  captured_sensitive_content=$false
  protected_native_smoke_modified=$false
} | ConvertTo-Json -Depth 6 -Compress
