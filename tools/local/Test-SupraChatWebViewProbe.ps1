param(
    [string]$DotNet = 'dotnet',
    [Parameter(Mandatory = $true)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
$probeRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$probeDll = Join-Path $probeRoot 'SupraChat.WebViewProbe/bin/Release/net10.0-windows/SupraChat.WebViewProbe.dll'
$probeOutput = [IO.Path]::GetFullPath($ReceiptPath)
if (Test-Path -LiteralPath $probeOutput) { throw 'Use a fresh receipt path to preserve previous evidence.' }
if (!(Test-Path -LiteralPath $probeDll)) { throw 'Build SupraChat.WebViewProbe in Release first.' }
$probeLauncher = (Get-Command $DotNet -ErrorAction Stop).Source
$probeStart = [Diagnostics.ProcessStartInfo]::new()
$probeStart.FileName = $probeLauncher
$probeStart.UseShellExecute = $false
$probeStart.CreateNoWindow = $true
$probeStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$probeStart.WorkingDirectory = $probeRoot
$probeStart.ArgumentList.Add($probeDll)
$probeStart.ArgumentList.Add('--receipt')
$probeStart.ArgumentList.Add($probeOutput)
$probeProcess = [Diagnostics.Process]::Start($probeStart)
try {
    if (!$probeProcess.WaitForExit(45000)) {
        $probeProcess.Kill($true)
        throw 'Synthetic native WebView probe timed out.'
    }
    if (!(Test-Path -LiteralPath $probeOutput)) { throw 'Probe did not produce a receipt.' }
    $probeJson = [IO.File]::ReadAllText($probeOutput)
    foreach ($probeForbidden in @('synthetic-private-sentinel-51', 'http://', 'https://', 'Bearer ', 'Cookie:', 'Authorization:')) {
        if ($probeJson.Contains($probeForbidden)) { throw 'Probe receipt failed its privacy oracle.' }
    }
    $probeReceipt = $probeJson | ConvertFrom-Json
    if ($probeProcess.ExitCode -ne 0 -or !$probeReceipt.passed -or $probeReceipt.phase -ne 'verified') {
        throw ('Native probe did not qualify; inspect the sanitized receipt. Phase: ' + $probeReceipt.phase)
    }
    if ($probeReceipt.qualification_scope -ne 'NATIVE_SHARED_TRACE_SYNTHETIC_ONLY' -or
        $probeReceipt.production_admitted -or $probeReceipt.completeness -ne 'UNKNOWN' -or
        $probeReceipt.real_credentials -or $probeReceipt.real_chatgpt_traffic -or $probeReceipt.raw_payload_exported -or
        $probeReceipt.remote_debugging_listener -or $probeReceipt.details.concurrent_sessions -ne 2 -or
        $probeReceipt.details.detached_callbacks -ne 0 -or !$probeReceipt.details.progressive_stream_acknowledged -or $probeReceipt.details.typed_loss -ne 0 -or
        $probeReceipt.details.shared_trace_schema -ne 'suprachat-trace/v1' -or !$probeReceipt.details.scope_exit_detached -or
        !$probeReceipt.details.wrong_session_refused -or !$probeReceipt.details.first_party_admission_refused) {
        throw 'Probe receipt scope/qualification contract failed.'
    }
    [ordered]@{
        ok = $true
        qualification_scope = $probeReceipt.qualification_scope
        stream_lifecycle_disagreement = $probeReceipt.stream_lifecycle_disagreement
        production_admitted = $false
        assembly_sha256 = (Get-FileHash -LiteralPath $probeDll -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json
}
finally { $probeProcess.Dispose() }
