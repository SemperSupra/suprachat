#Requires -Version 5.1
<#
.SYNOPSIS
  Locally observe fixed structural properties of a SupraChat UIA tree.
.DESCRIPTION
  No UI text/value/window names, screenshots, cookies, auth tokens, DOM,
  clipboard, controls, remote debugging, network calls, or process mutation.
  Signed-in state is OPERATOR ATTESTED, never independently verified.
  SyntheticFixture is solely for disposable public CI app qualification.
#>
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$ProcessId,
 [ValidateRange(1,20)][int]$Samples=6,
 [ValidateRange(250,3000)][int]$IntervalMs=1000,
 [switch]$SyntheticFixture,
 [switch]$OperatorAttestsSignedIn
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if ($SyntheticFixture -and $OperatorAttestsSignedIn) {
 throw 'MUTUALLY_EXCLUSIVE_EVIDENCE_CLASS'
}
if (-not $SyntheticFixture -and -not $OperatorAttestsSignedIn) {
 throw 'EXPLICIT_OPERATOR_AUTH_ATTESTATION_REQUIRED'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p=Get-Process -Id $ProcessId -ErrorAction Stop
if ($p.ProcessName -ne 'SupraChat') { throw 'REFUSED_NON_SUPRACHAT_PROCESS' }
$initialStart=$p.StartTime.ToUniversalTime()
$anyRoot=$false
$rootValid=$false
$chat=$false
$sessions=$false
$maxDocs=0
$maxDesc=0
$errors=0
for($sample=1;$sample -le $Samples;$sample++){
 try {
  $p=Get-Process -Id $ProcessId -ErrorAction Stop
  if ($p.ProcessName -ne 'SupraChat' -or $p.StartTime.ToUniversalTime() -ne $initialStart) {
   throw 'UNEXPECTED_PROCESS_IDENTITY'
  }
  $p.Refresh()
  if ($p.HasExited) { throw 'PROCESS_EXITED' }
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
   $root=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
   if ($null -ne $root) {
    $anyRoot=$true
    if ($root.Current.AutomationId -eq 'SupraChat.MainWindow' -and
        $root.Current.ProcessId -eq $ProcessId) {
     $rootValid=$true
     $queue=New-Object System.Collections.Queue
     $queue.Enqueue($root)
     $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
     $count=0
     $docs=0
     while ($queue.Count -gt 0 -and $count -lt 3000) {
      $parent=[System.Windows.Automation.AutomationElement]$queue.Dequeue()
      $child=$walker.GetFirstChild($parent)
      while ($null -ne $child -and $count -lt 3000) {
       $count++
       # Compare fixed IDs; never serialize or log raw provider strings.
       $id=$child.Current.AutomationId
       if ($id -eq 'Tab.ChatGPT') { $chat=$true }
       if ($id -eq 'Tab.Sessions') { $sessions=$true }
       if ($child.Current.ControlType -eq [System.Windows.Automation.ControlType]::Document) {
        $docs++
       }
       $queue.Enqueue($child)
       $child=$walker.GetNextSibling($child)
      }
     }
     if($count -gt $maxDesc){$maxDesc=$count}
     if($docs -gt $maxDocs){$maxDocs=$docs}
    }
   }
  }
 } catch {
  # Never serialize exception text or paths; errors are counted only.
  $errors++
 }
 if ($sample -lt $Samples) { Start-Sleep -Milliseconds $IntervalMs }
}
$evidence='PUBLIC_SYNTHETIC_NATIVE'
$auth='NOT_ASSESSED'
$observer='PUBLIC_WINDOWS_TEST'
if ($OperatorAttestsSignedIn) {
 $evidence='USER_ATTESTED_LOCAL_UIA'
 $auth='USER_ATTESTED_ONLY'
 $observer='LOCAL_WINDOWS_READONLY'
}
[pscustomobject]@{
 schema='suprachat-observable-surface/v1'
 evidence_class=$evidence
 authentication_claim=$auth
 observer=$observer
 capability=[ordered]@{
  observe_environment=$true
  inspect_ui=$true
  compare_state=$true
  remote_control=$false
 }
 measurements=[ordered]@{
  root_present=$anyRoot
  root_id_valid=$rootValid
  tab_chatgpt_present=$chat
  tab_sessions_present=$sessions
  webview_document_count=$maxDocs
  uia_descendant_count=$maxDesc
  sample_count=$Samples
  provider_error_count=$errors
 }
 privacy=[ordered]@{
  user_content=$false
  cookies=$false
  tokens=$false
  dom_text=$false
  screenshots=$false
  raw_trace=$false
  window_titles=$false
 }
} | ConvertTo-Json -Depth 5 -Compress
