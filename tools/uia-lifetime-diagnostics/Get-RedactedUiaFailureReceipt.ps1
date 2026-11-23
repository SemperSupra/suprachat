#requires -Version 5.1
<#
.SYNOPSIS
  R50 public-hosted read-only startup symptom receipt after GUI/UIA failure.
.DESCRIPTION
  Fixed categorical observations only. Never emits app logs, exception strings,
  personal paths, window identifiers, event bodies, or credentials.
  A symptom receipt cannot waive failed UIA/product qualification.
#>
[CmdletBinding()]
param(
    [string]$StartupLog,
    [string]$StdoutLog,
    [string]$StderrLog,
    [string]$EventLog
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$maximumBytes = 1048576

function Inspect-File {
    param([string]$Path)
    $result = [ordered]@{
        present = $false
        oversized = $false
        nonempty = $false
        content = ''
    }
    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $result
    }
    $item = Get-Item -LiteralPath $Path
    $result.present = $true
    $result.nonempty = ($item.Length -gt 0)
    if ($item.Length -gt $maximumBytes) {
        $result.oversized = $true
        return $result
    }
    $result.content = [IO.File]::ReadAllText($item.FullName)
    return $result
}

$startup = Inspect-File -Path $StartupLog
$stdout = Inspect-File -Path $StdoutLog
$stderr = Inspect-File -Path $StderrLog
$events = Inspect-File -Path $EventLog

$eventLineCount = 0
if ($events.present -and -not $events.oversized -and $events.nonempty) {
    # Deliberately count bounded rows only; do not parse or echo event payloads.
    $eventLineCount = @($events.content -split "\r?\n" |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    if ($eventLineCount -gt 2048) { $eventLineCount = 2048 }
}

$receipt = [ordered]@{
    schema = 'suprachat-redacted-uia-startup-observation/v1'
    evidence_class = 'PUBLIC_RUNNER_SYMPTOM_ONLY'
    startup_log_present = [bool]$startup.present
    startup_log_oversized = [bool]$startup.oversized
    managed_main_entered_observed = [bool]($startup.content.Contains('managed-main-entered'))
    desktop_lifetime_returned_observed = [bool]($startup.content.Contains('desktop-lifetime-returned'))
    fatal_startup_exception_marker_observed = [bool]($startup.content.Contains('fatal-startup-exception'))
    stdout_present = [bool]$stdout.present
    stdout_nonempty = [bool]$stdout.nonempty
    stdout_oversized = [bool]$stdout.oversized
    stderr_present = [bool]$stderr.present
    stderr_nonempty = [bool]$stderr.nonempty
    stderr_oversized = [bool]$stderr.oversized
    diagnostic_event_file_present = [bool]$events.present
    diagnostic_event_file_oversized = [bool]$events.oversized
    diagnostic_event_count_capped = [int]$eventLineCount
    root_cause = 'UNKNOWN'
    ui_automation_passed = $false
    privileged_capture_performed = $false
    credential_or_raw_log_exported = $false
    failed_qualification_gate_waived = $false
    external_tool_action_authorized = $false
}
$receipt | ConvertTo-Json -Depth 4 -Compress
