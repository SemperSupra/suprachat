#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path -Parent $PSScriptRoot) 'Get-RedactedUiaFailureReceipt.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('suprachat-r50-uia-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $startup = Join-Path $temp 'startup.txt'
    $stdout = Join-Path $temp 'stdout.txt'
    $stderr = Join-Path $temp 'stderr.txt'
    $events = Join-Path $temp 'events.jsonl'
    # Synthetic secrets and filenames are deliberately present in the input;
    # none may appear in the categorical receipt.
    @(
        'managed-main-entered framework=10 os=synthetic',
        'fatal-startup-exception',
        'System.InvalidOperationException: TOKEN-SECRET-DO-NOT-EMIT',
        'at C:\SENSITIVE-USERNAME\private\source.cs'
    ) | Set-Content -LiteralPath $startup -Encoding UTF8
    'PRIVATE-USER-STDOUT-SECRET' | Set-Content -LiteralPath $stdout -Encoding UTF8
    'COOKIE-PRIVATE-STDERR-SECRET' | Set-Content -LiteralPath $stderr -Encoding UTF8
    @('{"private":"SECRET-EVENT-1"}','{"private":"SECRET-EVENT-2"}') |
        Set-Content -LiteralPath $events -Encoding UTF8
    $raw = & $tool -StartupLog $startup -StdoutLog $stdout -StderrLog $stderr -EventLog $events
    $d = $raw | ConvertFrom-Json
    if ($d.schema -ne 'suprachat-redacted-uia-startup-observation/v1' -or
        -not $d.startup_log_present -or
        -not $d.managed_main_entered_observed -or
        -not $d.fatal_startup_exception_marker_observed -or
        $d.desktop_lifetime_returned_observed -or
        $d.diagnostic_event_count_capped -ne 2 -or
        $d.root_cause -ne 'UNKNOWN' -or
        $d.ui_automation_passed -or
        $d.failed_qualification_gate_waived -or
        $d.credential_or_raw_log_exported) {
        throw 'Redacted startup receipt contract failed'
    }
    foreach ($secret in @('TOKEN-SECRET','SENSITIVE-USERNAME','COOKIE-PRIVATE',
                          'PRIVATE-USER','SECRET-EVENT','source.cs')) {
        if ($raw.Contains($secret)) { throw 'Raw secret leaked into receipt' }
    }
    $absent = (& $tool -StartupLog (Join-Path $temp 'absent.txt') |
        ConvertFrom-Json)
    if ($absent.startup_log_present -or $absent.fatal_startup_exception_marker_observed) {
        throw 'Missing observations were fabricated'
    }
    $large = Join-Path $temp 'large.txt'
    [IO.File]::WriteAllText($large,('private-payload' * 100000))
    $bounded = (& $tool -StartupLog $large | ConvertFrom-Json)
    if (-not $bounded.startup_log_oversized -or $bounded.fatal_startup_exception_marker_observed) {
        throw 'Oversized input was parsed or not classified'
    }
    Write-Output '{"schema":"suprachat-r50-uia-privacy-test/v1","result":"PASS","synthetic_only":true,"ui_automation_passed":false,"root_cause":"UNKNOWN"}'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
