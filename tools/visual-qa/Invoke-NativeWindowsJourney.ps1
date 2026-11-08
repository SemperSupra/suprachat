# Actual native Windows UIA task journey. Public runner, synthetic command, no credentials.
$ErrorActionPreference='Stop'
$repo = (Get-Location).Path
$out = Join-Path $repo 'qa-evidence/visual/192-20261108/windows-journey'
New-Item -Force -ItemType Directory -Path $out | Out-Null
$manifest = Join-Path $out 'journey.txt'
'source=bedfb07c2847f0ade4cd4dc39ed956f59ad30c26' | Set-Content $manifest
$env:SUPRACHAT_STARTUP_LOG = Join-Path $env:RUNNER_TEMP 'visual-journey-startup.log'
$app = Join-Path $repo 'SupraChat/bin/Release/net10.0'
$p=Start-Process -FilePath 'dotnet' -ArgumentList 'SupraChat.dll' -WorkingDirectory $app -PassThru
try {
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  Add-Type -AssemblyName System.Drawing
  Add-Type -AssemblyName System.Windows.Forms
  Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VisualQaWindow {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);\n [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);\n [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
}
'@
  $handle=[IntPtr]::Zero
  foreach ($i in 1..30) {
    Start-Sleep -Milliseconds 700
    $p.Refresh()
    if ($p.HasExited) { break }
    $handle=$p.MainWindowHandle
    if ($handle -ne [IntPtr]::Zero) { break }
  }
  if ($handle -eq [IntPtr]::Zero) {
    'journey=NO_WINDOW' | Add-Content $manifest
    exit 0
  }
  [VisualQaWindow]::ShowWindow($handle,9) | Out-Null
  [VisualQaWindow]::SetWindowPos($handle,[IntPtr]::new(-1),8,8,1024,720,0x0040) | Out-Null\n  [VisualQaWindow]::BringWindowToTop($handle) | Out-Null\n  [VisualQaWindow]::SetForegroundWindow($handle) | Out-Null
  Start-Sleep -Seconds 1
  $root=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
  function FindUi([string] $id) {
    $cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $result=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)
    if ($null -eq $result) { throw "UI control unavailable: $id" }
    return $result
  }
  function SelectTab([string] $id) {
    $tab=FindUi $id
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
  }
  function SetUi([string] $id, [string] $value) {
    $el=FindUi $id
    $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
  }
  function ReadUi([string] $id) {
    $el=FindUi $id
    return $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
  }
  function ClickUi([string] $id) {
    $el=FindUi $id
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  }
  function Capture([string] $label) {
    Start-Sleep -Seconds 1
    $bounds=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp=New-Object System.Drawing.Bitmap($bounds.Width,$bounds.Height)
    $g=[System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location,[System.Drawing.Point]::Empty,$bounds.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $out ($label+'.png')),[System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "capture=$label" | Add-Content $manifest
  }

  SelectTab 'Tab.Sessions'
  Capture '01-sessions-900x650-before-selection'
  SetUi 'Terminal.CommandInput' 'Write-Output PRESESSION_VISUAL_QA'
  ClickUi 'Terminal.Run'
  Start-Sleep -Seconds 3
  $pre=ReadUi 'Terminal.ConsoleOutput'
  'pre_session_command_executed=' + $pre.Contains('PRESESSION_VISUAL_QA') | Add-Content $manifest
  Capture '02-pre-session-command'

  $workspace=Join-Path $env:RUNNER_TEMP 'suprachat-qa-workspace'
  New-Item -ItemType Directory -Force -Path $workspace | Out-Null
  SetUi 'Sessions.NewTitle' 'Visual QA work session'
  SetUi 'Sessions.NewWorkspace' $workspace
  ClickUi 'Sessions.Create'
  Start-Sleep -Seconds 3
  $sessionList=FindUi 'Sessions.List'
  $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
  $items=$sessionList.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
  'session_list_items=' + $items.Count | Add-Content $manifest
  Capture '03-session-created'

  SetUi 'Terminal.CommandInput' 'Write-Output POSTSESSION_VISUAL_QA'
  ClickUi 'Terminal.Run'
  Start-Sleep -Seconds 3
  $post=ReadUi 'Terminal.ConsoleOutput'
  'post_session_command_output=' + $post.Contains('POSTSESSION_VISUAL_QA') | Add-Content $manifest
  Capture '04-command-executed'

  ClickUi 'Terminal.SendLastOutput'
  Start-Sleep -Seconds 1
  $draft=ReadUi 'Sessions.DraftInput'
  'output_staged_to_draft_field=' + $draft.Contains('POSTSESSION_VISUAL_QA') | Add-Content $manifest
  Capture '05-output-in-local-draft'

  ClickUi 'Sessions.ReviewToChat'
  Start-Sleep -Seconds 1
  ClickUi 'Sessions.StageDraft'
  Start-Sleep -Seconds 1
  $chatDraft=ReadUi 'Sessions.ChatDraft'
  'chat_stage_nonempty=' + (-not [string]::IsNullOrWhiteSpace($chatDraft)) | Add-Content $manifest
  'actual_chat_submission_attempted=False' | Add-Content $manifest
  Capture '06-local-chat-draft-staged'

  [VisualQaWindow]::SetWindowPos($handle,[IntPtr]::Zero,8,8,1024,720,0x0040) | Out-Null
  Capture '07-session-1024x720'
  'journey=COMPLETE_NATIVE_UIA' | Add-Content $manifest
} catch {
  'journey=INCOMPLETE' | Add-Content $manifest
  'failed_step_type=' + $_.Exception.GetType().Name | Add-Content $manifest
  'failed_step_message=' + ($_.Exception.Message -replace '[^a-zA-Z0-9 .:_-]','?') | Add-Content $manifest
} finally {
  $p.Refresh()
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
