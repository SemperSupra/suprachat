$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-SupraChatStartupClassification.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('suprachat-uia-classify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$path = Join-Path $root 'startup.log'
try {
    $missing = Get-SupraChatStartupClassification -Path $path
    if ($missing.LogPresent -or $missing.FatalMarker -or $missing.ExceptionType -ne 'UNAVAILABLE') {
        throw 'Missing-log classification violated.'
    }

    Set-Content -LiteralPath $path -Encoding UTF8 -Value '2026-11-07T10:00:00.0000000+00:00 managed-main-entered'
    $healthy = Get-SupraChatStartupClassification -Path $path
    if (-not $healthy.LogPresent -or $healthy.FatalMarker -or $healthy.ExceptionType -ne 'UNAVAILABLE') {
        throw 'Healthy-start classification violated.'
    }

    @(
        '2026-11-07T10:00:00.0000000+00:00 managed-main-entered'
        '2026-11-07T10:00:01.0000000+00:00 fatal-startup-exception'
        '2026-11-07T10:00:01.0000000+00:00 System.InvalidOperationException: VERY_SECRET_VALUE /users/sensitive/path'
        '   at Private.Internal.Stack()'
    ) | Set-Content -LiteralPath $path -Encoding UTF8
    $fatal = Get-SupraChatStartupClassification -Path $path
    if (-not $fatal.LogPresent -or -not $fatal.FatalMarker -or
        $fatal.ExceptionType -cne 'System.InvalidOperationException') {
        throw 'Fatal-start exception type classification violated.'
    }
    $serialized = $fatal | ConvertTo-Json -Compress
    if ($serialized -match 'VERY_SECRET_VALUE|sensitive|Private\.Internal') {
        throw 'Classifier leaked raw exception content.'
    }

    @(
        '2026-11-07T10:00:01.0000000+00:00 fatal-startup-exception'
        '2026-11-07T10:00:01.0000000+00:00 Evil.InjectedException: SECRET'
    ) | Set-Content -LiteralPath $path -Encoding UTF8
    $untrusted = Get-SupraChatStartupClassification -Path $path
    if (-not $untrusted.FatalMarker -or $untrusted.ExceptionType -ne 'UNAVAILABLE') {
        throw 'Unrecognized exception namespace must not be emitted.'
    }

    @(
        '2026-11-07T10:00:01.0000000+00:00 System.ArgumentException: not a fatal startup record'
        '2026-11-07T10:00:02.0000000+00:00 managed-main-entered'
    ) | Set-Content -LiteralPath $path -Encoding UTF8
    $noMarker = Get-SupraChatStartupClassification -Path $path
    if ($noMarker.FatalMarker -or $noMarker.ExceptionType -ne 'UNAVAILABLE') {
        throw 'An exception without the exact fatal marker must be ignored.'
    }

    Write-Host 'Nonsecret UIA startup classification PS5.1 fixtures PASS (5 cases).'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
