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
        !$probeReceipt.details.wrong_session_refused -or !$probeReceipt.details.first_party_admission_refused -or
        !$probeReceipt.details.duplicate_domain_owner_refused -or !$probeReceipt.details.controller_disposal_detached -or
        !$probeReceipt.details.repeated_old_disposal_preserved_replacement -or !$probeReceipt.details.foreign_redirect_revoked -or
        !$probeReceipt.details.consented_native.gateway_native_capture -or !$probeReceipt.details.consented_native.off_before_review -or
        !$probeReceipt.details.consented_native.historical_confirmation_refused -or !$probeReceipt.details.consented_native.idle_expiry_detached -or
        !$probeReceipt.details.consented_native.native_view_replacement_revoked -or !$probeReceipt.details.consented_native.blank_navigation_revoked -or
        !$probeReceipt.details.consented_native.stop_during_pending_enable_refused -or !$probeReceipt.details.consented_native.expiry_during_pending_enable_refused -or
        !$probeReceipt.details.consented_native.intentional_stop_not_loss -or !$probeReceipt.details.consented_native.first_end_reason_preserved -or
        !$probeReceipt.details.consented_native.cleanup_settled_before_summary -or
        !$probeReceipt.details.consented_native.idle_expiry_without_polling -or !$probeReceipt.details.consented_native.retired_timer_preserved_replacement -or
        !$probeReceipt.details.consented_native.early_timer_rescheduled -or !$probeReceipt.details.consented_native.renewed_lease_deadline_respected -or
        !$probeReceipt.details.native_review_ui.actual_review_controls -or !$probeReceipt.details.native_review_ui.acknowledgement_without_typing -or
        !$probeReceipt.details.native_review_ui.expired_review_disabled -or !$probeReceipt.details.native_review_ui.repeated_platform_getter_ready -or
        !$probeReceipt.details.native_review_ui.missing_native_view_explained -or !$probeReceipt.details.native_review_ui.reattached_view_ready -or
        !$probeReceipt.details.native_review_ui.capture_end_explained -or $probeReceipt.details.native_review_ui.first_party_live_executed -or
        !$probeReceipt.details.native_review_ui.settled_review_close -or !$probeReceipt.details.native_review_ui.duplicate_close_one_summary -or
        !$probeReceipt.details.native_review_ui.close_preserves_first_reason -or !$probeReceipt.details.native_review_ui.close_during_save -or
        !$probeReceipt.details.native_review_ui.close_prepared_or_off -or !$probeReceipt.details.native_review_ui.close_save_failure_reported -or
        !$probeReceipt.details.native_review_ui.synthetic_summary_distinct -or
        !$probeReceipt.details.native_review_ui.close_during_enable -or !$probeReceipt.details.native_review_ui.close_during_prepare -or
        !$probeReceipt.details.native_review_ui.owner_waits_for_review_close -or !$probeReceipt.details.native_review_ui.owner_close_default_off -or
        !$probeReceipt.details.native_review_ui.owner_without_review_unblocked -or
        $probeReceipt.details.consented_native.first_party_live_executed) {
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
