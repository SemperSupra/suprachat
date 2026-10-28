using System.Text.Json;
using System.Text.Json.Nodes;

namespace SupraChat.Core;

internal static class NativeMetadataSummary
{
    internal static JsonElement Create(NativeMetadataStatus status, TracePage page, string assemblyHash)
    {
        if (status.Scope != "SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN") throw new UnauthorizedAccessException("First-party summary scope refused.");
        return Project(status, page, assemblyHash, false);
    }
    internal static JsonElement CreateSynthetic(NativeMetadataStatus status, TracePage page, string assemblyHash)
    {
        if (status.Scope != "SYNTHETIC_LOOPBACK_NATIVE") throw new UnauthorizedAccessException("Synthetic summary scope refused.");
        return Project(status, page, assemblyHash, true);
    }
    /// <summary>
    /// Upgrade one existing owner-local aggregate atomically, not a second ledger.
    /// The older v2/v1 summaries remain readable; the only added data is the
    /// bounded, sanitized app-authored lifecycle projection.
    /// </summary>
    internal static JsonElement AttachLifecycle(JsonElement original, NativeMetadataLifecycleReadback lifecycle)
    {
        if (original.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Metadata aggregate required.");
        var schema = original.GetProperty("schema").GetString();
        var upgraded = schema switch
        {
            "suprachat-first-party-metadata-hil-summary/v2" => "suprachat-first-party-metadata-hil-summary/v3",
            "suprachat-synthetic-native-metadata-summary/v1" => "suprachat-synthetic-native-metadata-summary/v2",
            _ => throw new InvalidDataException("Unknown metadata aggregate version.")
        };
        if (lifecycle.Entries.Count is < 1 or > 24) throw new InvalidDataException("Lifecycle history bound refused.");
        // Reject stale or contradictory projections *before* creating the one
        // locally persisted aggregate. Independent consumer validation remains
        // separate; an internally generated record is not automatically proof.
        var ending = original.GetProperty("first_lease_end_reason").GetString();
        var last = lifecycle.Entries[^1];
        if (!string.Equals(last.EndReason, ending, StringComparison.Ordinal) ||
            last.Active != original.GetProperty("capture_active").GetBoolean() ||
            last.ObserverAttached != original.GetProperty("observer_attached").GetBoolean() ||
            last.CleanupComplete != original.GetProperty("cleanup_complete").GetBoolean() ||
            last.EnableCompleted != original.GetProperty("enable_completed").GetBoolean() ||
            last.DisableCompleted != original.GetProperty("disable_completed").GetBoolean() ||
            original.GetProperty("attaching").GetBoolean())
            throw new InvalidDataException("Lifecycle aggregate and final observation disagree.");
        var node = JsonNode.Parse(original.GetRawText())!.AsObject();
        node["schema"] = upgraded;
        node["lifecycle_readback"] = JsonNode.Parse(lifecycle.ToUiValue());
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement Project(NativeMetadataStatus status, TracePage page, string assemblyHash, bool synthetic)
    {
        return JsonSerializer.SerializeToElement(new
        {
            schema = synthetic ? "suprachat-synthetic-native-metadata-summary/v1" : "suprachat-first-party-metadata-hil-summary/v2",
            evidence = synthetic ? "SYNTHETIC_LOOPBACK_ONLY" : status.NativeEvents > 0 ? "FIRST_PARTY_LIVE_REDACTED" : "NO_FIRST_PARTY_OBSERVATIONS",
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
            signed_in_state = synthetic ? "NOT_APPLICABLE_SYNTHETIC" : "OPERATOR_CHECK_REQUIRED", provider_operation_verified = false,
            conversation_binding_verified = false, human_identity_verified = false,
            prompts_responses_headers_cookies_exported = false, automatic_upload = false
        });
    }
}
