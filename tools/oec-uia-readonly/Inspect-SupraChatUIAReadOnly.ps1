#Requires -Version 5.1
<#
.SYNOPSIS
  Observe the UI Automation tree of an explicitly admitted synthetic SupraChat process.
.DESCRIPTION
  Read-only fixed-ID inspection: no control invocation, text/value reads, process
  mutation, authentication, clipboard, input or network access. Output contains
  only bounded counts, booleans and exception *types*, not titles or UI content.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$ProcessId,
  [ValidateRange(1,20)][int]$Samples = 8,
  [ValidateRange(250,3000)][int]$IntervalMs = 1000,
  [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$ExpectedAutomationId = 'SupraChat.MainWindow',
  [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$TargetAutomationId = 'Tab.Sessions'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$initial=Get-Process -Id $ProcessId -ErrorAction Stop
if ($initial.ProcessName -ne 'SupraChat') {
  throw 'REFUSED_NON_SYNTHETIC_PROCESS'
}
$initialStart=$initial.StartTime.ToUniversalTime()

function Get-SyntheticTreeCount {
  param(
    [System.Windows.Automation.AutomationElement]$Root,
    [string]$TargetId,
    [ValidateRange(100,5000)][int]$Ceiling=3000
  )
  $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
  $queue=New-Object System.Collections.Queue
  $queue.Enqueue($Root)
  $count=0; $direct=0; $target=0
  while ($queue.Count -gt 0 -and $count -lt $Ceiling) {
    $parent=[System.Windows.Automation.AutomationElement]$queue.Dequeue()
    $isRoot=[object]::ReferenceEquals($parent,$Root)
    $child=$walker.GetFirstChild($parent)
    while ($null -ne $child -and $count -lt $Ceiling) {
      $count++
      if ($isRoot) { $direct++ }
      if ($child.Current.AutomationId -eq $TargetId) { $target++ }
      $queue.Enqueue($child)
      $child=$walker.GetNextSibling($child)
    }
  }
  [pscustomobject]@{
    direct_children=$direct
    descendants=$count
    target_matches=$target
    tree_truncated=($count -ge $Ceiling)
  }
}

$records=New-Object 'System.Collections.Generic.List[object]'
$clock=[System.Diagnostics.Stopwatch]::StartNew()
for($i=0; $i -lt $Samples; $i++) {
  $record=[ordered]@{
    sample=($i+1)
    elapsed_ms=[int]$clock.ElapsedMilliseconds
    process_alive=$false
    handle_nonzero=$false
    root_present=$false
    root_id_matches=$false
    root_process_matches=$null
    root_enabled=$null
    root_offscreen=$null
    direct_children=$null
    descendants=$null
    target_matches=$null
    tree_truncated=$null
    provider_error_class=$null
  }
  try {
    $p=Get-Process -Id $ProcessId -ErrorAction Stop
    if ($p.ProcessName -ne 'SupraChat' -or
        $p.StartTime.ToUniversalTime() -ne $initialStart) {
      throw 'UnexpectedProcessIdentity'
    }
    $record.process_alive=(-not $p.HasExited)
    $p.Refresh()
    $handle=$p.MainWindowHandle
    $record.handle_nonzero=([int64]$handle -ne 0)
    if ($record.handle_nonzero) {
      $root=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
      if ($null -ne $root) {
        $record.root_present=$true
        $record.root_id_matches=($root.Current.AutomationId -eq $ExpectedAutomationId)
        $record.root_process_matches=($root.Current.ProcessId -eq $ProcessId)
        if ($record.root_id_matches -and $record.root_process_matches) {
          $record.root_enabled=$root.Current.IsEnabled
          $record.root_offscreen=$root.Current.IsOffscreen
          $tree=Get-SyntheticTreeCount -Root $root -TargetId $TargetAutomationId
          $record.direct_children=$tree.direct_children
          $record.descendants=$tree.descendants
          $record.target_matches=$tree.target_matches
          $record.tree_truncated=$tree.tree_truncated
        }
      }
    }
  } catch {
    # Exception type only; messages/stacks can include host-specific data.
    $record.provider_error_class=$_.Exception.GetType().Name
  }
  $records.Add([pscustomobject]$record)
  if ($i -lt ($Samples-1)) { Start-Sleep -Milliseconds $IntervalMs }
}
$clock.Stop()
$valid=@($records | Where-Object { $_.root_id_matches -and $_.root_process_matches })
$rootFound=$valid.Count -gt 0
$rootEmpty=@($valid | Where-Object { $_.descendants -eq 0 }).Count -gt 0
$tabFound=@($valid | Where-Object { $_.target_matches -gt 0 }).Count -gt 0
$classification='UIA_ROOT_NOT_OBSERVED'
if ($rootFound) {
  $classification='ROOT_PRESENT_SEMANTIC_TREE_OBSERVED'
  if ($rootEmpty) { $classification='ROOT_PRESENT_DESCENDANTS_EMPTY_AT_SAMPLE' }
  if ($tabFound) { $classification='TARGET_TAB_OBSERVED' }
}
[pscustomobject]@{
  schema='suprachat-uia-readonly-diagnostic/v2'
  evidence_class='PUBLIC_WINDOWS_SYNTHETIC_OBSERVATION'
  samples=$records.ToArray()
  sample_count=$records.Count
  elapsed_total_ms=[int]$clock.ElapsedMilliseconds
  result_class=$classification
  root_observed=$rootFound
  empty_descendants_after_root=$rootEmpty
  tab_observed=$tabFound
  read_only=$true
  content_or_credentials_captured=$false
} | ConvertTo-Json -Depth 5 -Compress
