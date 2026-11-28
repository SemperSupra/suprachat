#requires -Version 5.1
<#
Fixed categorical observations only; never emits app logs, exception strings,
paths, identifiers, event bodies or credentials. A symptom receipt cannot
waive failed UIA/product qualification.
#>
[CmdletBinding()]
param([string]$StartupLog, [string]$StdoutLog, [string]$StderrLog, [string]$EventLog)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$maximumBytes = 1048576
function Inspect-File {
    param([string]$Path, [switch]$ReadContent)
    $result = [ordered]@{ present = $false; oversized = $false; nonempty = $false; content = '' }
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $result
    }
    $item = Get-Item -LiteralPath $Path
    $result.present = $true
    $result.nonempty = ($item.Length -gt 0)
    if ($item.Length -gt $maximumBytes) {
        $result.oversized = $true
        return $result
    }
    if ($ReadContent) {
        # Bound the actual read even if a process is still writing.
        $buffer = New-Object byte[] ($maximumBytes + 1)
        $stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $used = 0
            while ($used -lt $buffer.Length) {
                $n = $stream.Read($buffer, $used, $buffer.Length - $used)
                if ($n -eq 0) { break }
                $used += $n
            }
            if ($used -gt $maximumBytes) {
                $result.oversized = $true
                return $result
            }
            $result.content = [Text.Encoding]::UTF8.GetString($buffer, 0, $used)
        }
        finally { $stream.Dispose() }
    }
    return $result
}
$startup = Inspect-File -Path $StartupLog -ReadContent
$stdout = Inspect-File -Path $StdoutLog
$stderr = Inspect-File -Path $StderrLog
$events = Inspect-File -Path $EventLog -ReadContent
$eventLineCount = 0
if ($events.present -and -not $events.oversized -and $events.nonempty) {
    $eventLineCount = @($events.content -split "\r?\n" |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    if ($eventLineCount -gt 2048) { $eventLineCount = 2048 }
}
# Program.Log uses ISO-8601 "O" timestamp followed by the message.
# Only a producer-shaped marker and adjacent exception header qualify.
$stamp = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2}) '
$lines = @($startup.content.TrimStart([char]0xFEFF) -split "\r?\n")
$managed = $false
$returned = $false
$fatalCount = 0
$fatalIndex = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -cmatch ($stamp + 'managed-main-entered framework=')) { $managed = $true }
    if ($line -cmatch ($stamp + 'desktop-lifetime-returned$')) { $returned = $true }
    if ($line -cmatch ($stamp + 'fatal-startup-exception$')) { $fatalCount++; $fatalIndex = $i }
}
$category = 'UNAVAILABLE'
if ($fatalCount -gt 1) { $category = 'AMBIGUOUS_MARKERS' }
elseif ($fatalCount -eq 1 -and ($fatalIndex + 1) -lt $lines.Count) {
    if ($lines[$fatalIndex + 1] -cmatch ($stamp + '([A-Za-z0-9_.+]{1,200})(?::|$)')) {
        # Fixed literal categories, never a matched class name or namespace prefix.
        switch -CaseSensitive ($Matches[1]) {
            'System.InvalidOperationException' { $category = 'INVALID_OPERATION' }
            'System.ArgumentException' { $category = 'ARGUMENT' }
            'System.ArgumentNullException' { $category = 'ARGUMENT' }
            'System.ArgumentOutOfRangeException' { $category = 'ARGUMENT' }
            'System.NullReferenceException' { $category = 'NULL_REFERENCE' }
            'System.ObjectDisposedException' { $category = 'OBJECT_DISPOSED' }
            'System.IO.IOException' { $category = 'IO' }
            'System.IO.FileNotFoundException' { $category = 'IO' }
            'System.IO.FileLoadException' { $category = 'IO' }
            'System.IO.DirectoryNotFoundException' { $category = 'IO' }
            'System.UnauthorizedAccessException' { $category = 'ACCESS_DENIED' }
            'System.Runtime.InteropServices.COMException' { $category = 'COM' }
            'System.TypeInitializationException' { $category = 'TYPE_INITIALIZATION' }
            'System.DllNotFoundException' { $category = 'NATIVE_LIBRARY' }
            'System.BadImageFormatException' { $category = 'NATIVE_LIBRARY' }
            default { $category = 'OTHER_OR_UNRECOGNIZED' }
        }
    }
}
# Only the bounded pre-fatal producer marker is accepted. Never infer a
# throw frame from arbitrary stack text or trust arbitrary enum-like tokens.
$lastStage = 'UNAVAILABLE'
$outerComponent = 'UNAVAILABLE'
$innerComponent = 'UNAVAILABLE'
$innerKind = 'UNAVAILABLE'
$chainTruncated = $null
$stages = @('DesktopLifetimeEntering','XamlLoading','XamlLoaded','FrameworkInitialization',
    'MainWindowCreating','MainWindowAssigned','FrameworkInitialized',
    'StartupArgumentsEntering','StartupArgumentsReturned')
$components = @('NO_STACK','APP','AVALONIA_BASE','AVALONIA_CONTROLS','AVALONIA_WIN32',
    'AVALONIA_WEBVIEW','WEBVIEW2','DOTNET_RUNTIME','OTHER_MANAGED','UNAVAILABLE')
$kinds = @('NONE','UNAVAILABLE','COM','INVALID_OPERATION','OBJECT_DISPOSED','NULL_REFERENCE',
    'ARGUMENT','IO','TYPE_INITIALIZATION','ACCESS_DENIED','NATIVE_LIBRARY','OTHER')
if ($fatalCount -eq 1) {
    for ($i = 0; $i -lt $fatalIndex; $i++) {
        if ($lines[$i] -cmatch ($stamp + 'lifecycle-stage=([A-Za-z]{1,64})$')) {
            $candidate = $Matches[1]
            if ($candidate -cin $stages) { $lastStage = $candidate }
        }
    }
    if ($fatalIndex -gt 0 -and $lines[$fatalIndex - 1] -cmatch
        ($stamp + 'fatal-components outer=([A-Z_]{1,32}) inner=([A-Z_]{1,32}) inner-kind=([A-Z_]{1,32}) truncated=(YES|NO)$')) {
        $a = $Matches[1]; $b = $Matches[2]; $c = $Matches[3]; $d = $Matches[4]
        if ($a -cin $components -and ($b -eq 'NONE' -or $b -cin $components) -and
            $c -cin $kinds -and (($b -eq 'NONE') -eq ($c -eq 'NONE')) -and
            -not ($b -eq 'NONE' -and $d -eq 'YES')) {
            $outerComponent = $a; $innerComponent = $b; $innerKind = $c
            $chainTruncated = ($d -eq 'YES')
        }
    }
}
$receipt = [ordered]@{
    schema = 'suprachat-redacted-uia-startup-observation/v3'
    evidence_class = 'PUBLIC_RUNNER_SYMPTOM_ONLY'
    startup_log_present = [bool]$startup.present
    startup_log_oversized = [bool]$startup.oversized
    managed_main_entered_observed = [bool]$managed
    desktop_lifetime_returned_observed = [bool]$returned
    fatal_startup_exception_marker_observed = [bool]($fatalCount -gt 0)
    exception_category_observed = $category
    last_pre_fatal_lifecycle_stage = $lastStage
    outer_throw_component_observed = $outerComponent
    inner_throw_component_observed = $innerComponent
    inner_exception_category_observed = $innerKind
    inner_chain_truncated = $chainTruncated
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
