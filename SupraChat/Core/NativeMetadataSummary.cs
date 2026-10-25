using System.Text.Json;

namespace SupraChat.Core;

internal static class NativeMetadataSummary
{
    internal static JsonElement Create(NativeMetadataStatus status, TracePage page, string assemblyHash)
    {
        if (status.Scope != "SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN") throw new UnauthorizedAccessException("First-party summary scope refused.");
        return JsonSerializer.SerializeToElement(new
        {
            schema = "suprachat-first-party-metadata-hil-summary/v2",
            evidence = status.NativeEvents > 0 ? "FIRST_PARTY_LIVE_REDACTED" : "NO_FIRST_PARTY_OBSERVATIONS",
            scope = status.Scope, completeness = "UNKNOWN", assembly_sha256 = assemblyHash,
            observed_network_events = status.NativeEvents, native_loss = status.NativeLoss, capture_gap = page.CaptureGap,
            native_loss_semantics = "DIAGNOSTIC_INCIDENTS_NOT_LOST_EVENT_COUNT",
            diagnostics = status.Diagnostics,
            capture_active = status.Consent.CaptureActive, observer_attached = status.ObserverAttached, attaching = status.Attaching,
            first_lease_end_reason = status.EndReason.ToString(), cleanup_complete = status.CleanupComplete,
            enable_completed = status.EnableCompleted, disable_completed = status.DisableCompleted,
            stopped_interval_disclosed = page.Mode == TraceCaptureMode.Off,
            total_evicted_events = page.TotalEvictedEvents,
            retained_events = page.Events.Count,
            lifecycle = page.Events.GroupBy(e => e.EventKind).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            signed_in_state = "OPERATOR_CHECK_REQUIRED", provider_operation_verified = false,
            conversation_binding_verified = false, human_identity_verified = false,
            prompts_responses_headers_cookies_exported = false, automatic_upload = false
        });
    }
}
