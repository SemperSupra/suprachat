#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tool = Join-Path (Split-Path -Parent $PSScriptRoot) 'Get-RedactedUiaFailureReceipt.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('suprachat-r54-uia-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $startup = Join-Path $temp 'startup.txt'
    $stdout = Join-Path $temp 'stdout.txt'
    $stderr = Join-Path $temp 'stderr.txt'
    $events = Join-Path $temp 'events.jsonl'
    $ts = '2026-11-28T08:00:01.0000000+00:00 '
    @(
        ($ts + 'managed-main-entered framework=10 os=synthetic'),
        ($ts + 'lifecycle-stage=MainWindowAssigned'),
        ($ts + 'fatal-components outer=AVALONIA_BASE inner=WEBVIEW2 inner-kind=COM truncated=NO'),
        ($ts + 'fatal-startup-exception'),
        ($ts + 'System.InvalidOperationException: TOKEN-SECRET-DO-NOT-EMIT'),
        'at C:\SENSITIVE-USERNAME\private\source.cs'
    ) | Set-Content -LiteralPath $startup -Encoding UTF8
    'PRIVATE-USER-STDOUT-SECRET' | Set-Content -LiteralPath $stdout -Encoding UTF8
    'COOKIE-PRIVATE-STDERR-SECRET' | Set-Content -LiteralPath $stderr -Encoding UTF8
    @('{"private":"SECRET-EVENT-1"}','{"private":"SECRET-EVENT-2"}') |
        Set-Content -LiteralPath $events -Encoding UTF8
    $raw = & $tool -StartupLog $startup -StdoutLog $stdout -StderrLog $stderr -EventLog $events
    $d = $raw | ConvertFrom-Json
    if ($d.schema -ne 'suprachat-redacted-uia-startup-observation/v3' -or
        -not $d.startup_log_present -or -not $d.managed_main_entered_observed -or
        -not $d.fatal_startup_exception_marker_observed -or
        $d.desktop_lifetime_returned_observed -or $d.exception_category_observed -cne 'INVALID_OPERATION' -or
        $d.last_pre_fatal_lifecycle_stage -cne 'MainWindowAssigned' -or
        $d.outer_throw_component_observed -cne 'AVALONIA_BASE' -or
        $d.inner_throw_component_observed -cne 'WEBVIEW2' -or
        $d.inner_exception_category_observed -cne 'COM' -or $d.inner_chain_truncated -ne $false -or
        $d.diagnostic_event_count_capped -ne 2 -or $d.root_cause -ne 'UNKNOWN' -or
        $d.ui_automation_passed -or $d.failed_qualification_gate_waived -or $d.credential_or_raw_log_exported) {
        throw 'Redacted startup receipt contract failed'
    }
    foreach ($secret in @('TOKEN-SECRET','SENSITIVE-USERNAME','COOKIE-PRIVATE','PRIVATE-USER','SECRET-EVENT','source.cs')) {
        if ($raw.Contains($secret)) { throw 'Raw secret leaked into receipt' }
    }
    $absent = (& $tool -StartupLog (Join-Path $temp 'absent.txt') | ConvertFrom-Json)
    if ($absent.startup_log_present -or $absent.fatal_startup_exception_marker_observed -or
        $absent.exception_category_observed -ne 'UNAVAILABLE') { throw 'Missing observations were fabricated' }
    $large = Join-Path $temp 'large.txt'
    [IO.File]::WriteAllText($large,('private-payload' * 100000))
    $bounded = (& $tool -StartupLog $large | ConvertFrom-Json)
    if (-not $bounded.startup_log_oversized -or $bounded.fatal_startup_exception_marker_observed -or
        $bounded.exception_category_observed -ne 'UNAVAILABLE') { throw 'Oversized input was parsed' }
    $cases = @(
        @{ lines = @(($ts + 'fatal-startup-exception'), ($ts + 'System.SECRET_IN_NAMESPACEException: payload')); category = 'OTHER_OR_UNRECOGNIZED'; fatal = $true },
        @{ lines = @(($ts + 'fatal-startup-exception'), ($ts + 'Microsoft.SECRET_IN_NAMESPACEException: payload')); category = 'OTHER_OR_UNRECOGNIZED'; fatal = $true },
        @{ lines = @(($ts + 'fatal-startup-exception'), ($ts + 'Avalonia.SECRET_IN_NAMESPACEException: payload')); category = 'OTHER_OR_UNRECOGNIZED'; fatal = $true },
        @{ lines = @(($ts + 'message contains fatal-startup-exception'), ($ts + 'System.InvalidOperationException: payload')); category = 'UNAVAILABLE'; fatal = $false },
        @{ lines = @(($ts + 'fatal-startup-exception'), 'unstructured stack text', ($ts + 'System.InvalidOperationException: payload')); category = 'UNAVAILABLE'; fatal = $true },
        @{ lines = @(($ts + 'fatal-startup-exception'), ($ts + 'System.InvalidOperationException: payload'), ($ts + 'fatal-startup-exception')); category = 'AMBIGUOUS_MARKERS'; fatal = $true },
        @{ lines = @(($ts + 'fatal-startup-exception'), ($ts + 'System.Runtime.InteropServices.COMException: payload')); category = 'COM'; fatal = $true },
        @{ lines = @(($ts + 'desktop-lifetime-returned')); category = 'UNAVAILABLE'; fatal = $false }
    )
    foreach ($case in $cases) {
        $case.lines | Set-Content -LiteralPath $startup -Encoding UTF8
        $receipt = & $tool -StartupLog $startup
        $parsed = $receipt | ConvertFrom-Json
        if ($parsed.exception_category_observed -cne $case.category -or
            $parsed.fatal_startup_exception_marker_observed -ne $case.fatal -or
            $parsed.root_cause -ne 'UNKNOWN' -or $parsed.ui_automation_passed -or
            $receipt -match 'SECRET_IN_NAMESPACE|payload') {
            throw 'Fixed exception-category privacy/attribution contract failed'
        }
    }
    # Unknown component/stage tokens and marker-like exception messages cannot escape.
    @(
        ($ts + 'lifecycle-stage=PRIVATESTAGE'),
        ($ts + 'fatal-components outer=PRIVATE_COMPONENT inner=WEBVIEW2 inner-kind=COM truncated=NO'),
        ($ts + 'fatal-startup-exception'),
        ($ts + 'System.InvalidOperationException: payload'),
        ($ts + 'lifecycle-stage=MainWindowAssigned'),
        ($ts + 'fatal-components outer=APP inner=NONE inner-kind=NONE truncated=NO')
    ) | Set-Content -LiteralPath $startup -Encoding UTF8
    $raw = & $tool -StartupLog $startup
    $d = $raw | ConvertFrom-Json
    if ($d.last_pre_fatal_lifecycle_stage -ne 'UNAVAILABLE' -or
        $d.outer_throw_component_observed -ne 'UNAVAILABLE' -or $raw -match 'PRIVATE') {
        throw 'UNTRUSTED_COMPONENT_MARKER_ACCEPTED'
    }
    # A valid-looking marker without a matching fatal event is not evidence.
    @(
        ($ts + 'lifecycle-stage=MainWindowAssigned'),
        ($ts + 'fatal-components outer=APP inner=NONE inner-kind=NONE truncated=NO')
    ) | Set-Content -LiteralPath $startup -Encoding UTF8
    $d = (& $tool -StartupLog $startup | ConvertFrom-Json)
    if ($d.outer_throw_component_observed -ne 'UNAVAILABLE' -or
        $d.last_pre_fatal_lifecycle_stage -ne 'UNAVAILABLE') { throw 'UNBOUND_COMPONENT_MARKER' }
    Write-Output '{"schema":"suprachat-r55-uia-privacy-test/v3","result":"PASS","synthetic_only":true,"ui_automation_passed":false,"root_cause":"UNKNOWN"}'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
