param([string]$DotNet = 'dotnet', [Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$oracleRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$oracleAssembly = Join-Path $oracleRoot 'SupraChat.WebViewOracle/bin/Release/net10.0-windows/SupraChat.WebViewOracle.dll'
$oracleOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($oracleOutput) | Out-Null
$oracleReceipts = @()
foreach ($oracleEngine in @('playwright','webview2')) {
    $oracleReceipt = Join-Path $oracleOutput ($oracleEngine + '.json')
    if (Test-Path -LiteralPath $oracleReceipt) { throw 'Use fresh oracle receipt paths.' }
    $oracleStart = [Diagnostics.ProcessStartInfo]::new()
    $oracleStart.FileName = (Get-Command $DotNet -ErrorAction Stop).Source
    $oracleStart.UseShellExecute = $false
    $oracleStart.CreateNoWindow = $true
    $oracleStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $oracleStart.ArgumentList.Add($oracleAssembly)
    $oracleStart.ArgumentList.Add('--engine')
    $oracleStart.ArgumentList.Add($oracleEngine)
    $oracleStart.ArgumentList.Add('--receipt')
    $oracleStart.ArgumentList.Add($oracleReceipt)
    $oracleProcess = [Diagnostics.Process]::Start($oracleStart)
    try {
        $oracleTimer = [Diagnostics.Stopwatch]::StartNew()
        $finished = $oracleProcess.WaitForExit(35000)
        $oracleTimer.Stop()
        if (!$finished) {
            # Fixed-only first-failure evidence. Never print subprocess output,
            # paths, URLs, browser profiles or fixture contents.
            $cpuMs = $null
            try { $cpuMs = [int][Math]::Round($oracleProcess.TotalProcessorTime.TotalMilliseconds) } catch {}
            $receiptExists = Test-Path -LiteralPath $oracleReceipt -PathType Leaf
            $killConfirmed = $false
            try {
                $oracleProcess.Kill($true)
                $killConfirmed = $oracleProcess.WaitForExit(5000)
            } catch {}
            $observation = [ordered]@{
                schema = 'suprachat-browser-oracle-first-failure/v1'
                stage = 'SUBPROCESS_TIMEOUT'
                engine = $oracleEngine
                allotted_ms = 35000
                elapsed_ms = [int]$oracleTimer.ElapsedMilliseconds
                child_cpu_ms = $cpuMs
                receipt_present = [bool]$receiptExists
                owned_process_exit_confirmed = [bool]$killConfirmed
                raw_payload_exported = $false
                cause_established = $false
                qualification_passed = $false
            }
            Write-Host ($observation | ConvertTo-Json -Compress)
            throw ('Independent browser oracle timed out: ' + $oracleEngine)
        }
        if (!(Test-Path -LiteralPath $oracleReceipt -PathType Leaf)) {
            Write-Host (@{
                schema = 'suprachat-browser-oracle-first-failure/v1'
                stage = 'RECEIPT_MISSING'
                engine = $oracleEngine
                process_exit_code = [int]$oracleProcess.ExitCode
                raw_payload_exported = $false
                cause_established = $false
                qualification_passed = $false
            } | ConvertTo-Json -Compress)
            throw ('Independent browser oracle receipt missing: ' + $oracleEngine)
        }
        $oracleJson = [IO.File]::ReadAllText($oracleReceipt)
        foreach ($oracleForbidden in @('synthetic-private-sentinel-51', 'http://', 'https://', 'Bearer ', 'Cookie:', 'Authorization:')) {
            if ($oracleJson.Contains($oracleForbidden)) { throw 'Independent oracle receipt privacy check failed.' }
        }
        $oracleDoc = $oracleJson | ConvertFrom-Json
        $expectedEngine = if ($oracleEngine -ceq 'playwright') { 'playwright-chromium' } else { 'microsoft-webview2' }
        if ($oracleDoc.schema -cne 'suprachat-independent-browser-oracle/v1' -or
            $oracleDoc.engine -cne $expectedEngine -or $oracleDoc.fixture -cne 'SYNTHETIC_LOOPBACK_ONLY' -or
            $oracleDoc.raw_payload_exported -isnot [bool] -or $oracleDoc.raw_payload_exported -or
            $oracleDoc.production_admitted -isnot [bool] -or $oracleDoc.production_admitted) {
            throw 'Independent browser oracle scope contract refused.'
        }
        if ($oracleProcess.ExitCode -ne 0 -or !$oracleDoc.passed -or !$oracleDoc.fixture_complete -or $oracleDoc.production_admitted) {
            throw ('Independent browser oracle failed in phase ' + $oracleDoc.phase)
        }
        if (($oracleDoc.counts.'loadingFinished:stream' + $oracleDoc.counts.'loadingFailed:stream') -ne 1) {
            throw 'Independent oracle did not observe exactly one terminal stream event.'
        }
        $oracleReceipts += $oracleDoc
    }
    finally { $oracleProcess.Dispose() }
}
$comparison = [ordered]@{
    schema = 'suprachat-browser-comparison/v1'
    passed = $true
    same_synthetic_fixture = $true
    playwright_stream_finished = ($oracleReceipts[0].counts.'loadingFinished:stream' -eq 1)
    webview2_stream_finished = ($oracleReceipts[1].counts.'loadingFinished:stream' -eq 1)
    webview2_stream_aborted = ($oracleReceipts[1].counts.'aborted:stream' -eq 1)
    versions = @($oracleReceipts.runtime_version)
    cause_established = $false
    production_admitted = $false
} | ConvertTo-Json
$comparisonStream = [IO.FileStream]::new((Join-Path $oracleOutput 'comparison.json'),
    [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$comparisonWriter = [IO.StreamWriter]::new($comparisonStream)
try { $comparisonWriter.Write($comparison) } finally { $comparisonWriter.Dispose() }
'Independent Playwright / Microsoft-only WebView2 comparison PASS (synthetic observation only).'
