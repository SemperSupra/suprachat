using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupraChat.Core;

/// <summary>
/// Architectural implementation of Campaign Package P3:
/// Synthetic Chat-vs-Work / UNKNOWN / CONFLICT experience mode detector and fail-closed submit policy.
/// Enforces fail-closed admission, strict origin boundaries, lease gating, and 3-audience projection.
/// </summary>
public static class BrowserExperienceModePolicy
{
    public const string Schema = "suprachat-mode-policy-candidate/v1";
    public const string Origin = "https://chatgpt.com";

    private static readonly Dictionary<string, (string Chat, string Work)> SupportedLocales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ("Chat", "Work"),
        ["de"] = ("Chat", "Arbeit"),
        ["th"] = ("\u0e41\u0e0a\u0e17", "\u0e07\u0e32\u0e19")
    };

    public static readonly IReadOnlyList<string> ValidAudiences = new[] { "human", "automation", "agent" };

    public sealed record ControlElement(
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("visible")] bool? Visible,
        [property: JsonPropertyName("selected")] bool? Selected);

    public sealed record CandidatePayload(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("origin")] string? Origin,
        [property: JsonPropertyName("session")] string? Session,
        [property: JsonPropertyName("epoch")] long? Epoch,
        [property: JsonPropertyName("timestamp")] long? Timestamp,
        [property: JsonPropertyName("locale")] string? Locale,
        [property: JsonPropertyName("controls")] IReadOnlyList<ControlElement>? Controls);

    public sealed record HostWorkLease(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("session")] string? Session,
        [property: JsonPropertyName("epoch")] long? Epoch,
        [property: JsonPropertyName("confirmed")] bool? Confirmed,
        [property: JsonPropertyName("expires")] long? Expires);

    public sealed record PreflightRequest(
        string Intent,
        CandidatePayload? Candidate,
        string? Session,
        long? Epoch,
        long? Now,
        HostWorkLease? Lease = null);

    public sealed record PreflightDecision(
        [property: JsonPropertyName("admission")] string Admission,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("effect")] string Effect);

    public sealed record AudienceDecision(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("admission")] string Admission,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("effect")] string Effect,
        [property: JsonPropertyName("evidence")] string Evidence,
        [property: JsonPropertyName("adapter")] string Adapter,
        [property: JsonPropertyName("audience")] string Audience);

    public static string Observe(CandidatePayload? candidate)
    {
        if (candidate is null ||
            candidate.Source != "SYNTHETIC_TYPED_ADAPTER" ||
            candidate.Origin != Origin ||
            string.IsNullOrEmpty(candidate.Session) ||
            !candidate.Epoch.HasValue || candidate.Epoch < 0 ||
            !candidate.Timestamp.HasValue || candidate.Timestamp < 0 ||
            string.IsNullOrEmpty(candidate.Locale) ||
            !SupportedLocales.TryGetValue(candidate.Locale, out var labels) ||
            candidate.Controls is null || candidate.Controls.Count > 16)
        {
            return "UNKNOWN";
        }

        var tabs = candidate.Controls.Where(x =>
            x != null &&
            x.Scope == "experience-switch" &&
            x.Role == "tab" &&
            x.Visible == true &&
            x.Selected.HasValue).ToList();

        var chatTabs = tabs.Where(x => string.Equals(x.Label, labels.Chat, StringComparison.Ordinal)).ToList();
        var workTabs = tabs.Where(x => string.Equals(x.Label, labels.Work, StringComparison.Ordinal)).ToList();

        if (chatTabs.Count > 1 || workTabs.Count > 1)
            return "CONFLICT";

        if (chatTabs.Count != 1 || workTabs.Count != 1)
            return "UNKNOWN";

        var chatSelected = chatTabs[0].Selected!.Value;
        var workSelected = workTabs[0].Selected!.Value;

        if (chatSelected == workSelected)
            return chatSelected ? "CONFLICT" : "UNKNOWN";

        return chatSelected ? "CHAT" : "WORK";
    }

    public static PreflightDecision Preflight(PreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Intent is not ("CHAT" or "WORK"))
            return Deny("INVALID_INTENT");

        if (string.IsNullOrEmpty(request.Session) ||
            !request.Epoch.HasValue ||
            !request.Now.HasValue || request.Now < 0)
        {
            return Deny("INVALID_HOST");
        }

        var candidate = request.Candidate;
        if (candidate is null || candidate.Session != request.Session || candidate.Epoch != request.Epoch)
            return Deny("SESSION_OR_NAVIGATION_MISMATCH");

        if (!candidate.Timestamp.HasValue || candidate.Timestamp > request.Now || request.Now - candidate.Timestamp > 2000)
            return Deny("STALE");

        var actual = Observe(candidate);
        if (actual is "UNKNOWN" or "CONFLICT")
            return Deny(actual);

        if (!string.Equals(actual, request.Intent, StringComparison.Ordinal))
            return Deny("MODE_MISMATCH");

        if (request.Intent == "WORK")
        {
            var lease = request.Lease;
            if (lease is null ||
                lease.Type != "EXPLICIT_HOST_WORK_LEASE" ||
                lease.Session != request.Session ||
                lease.Epoch != request.Epoch ||
                lease.Confirmed != true ||
                !lease.Expires.HasValue || lease.Expires <= request.Now)
            {
                return Deny("WORK_APPROVAL_REQUIRED");
            }
        }

        return new PreflightDecision("HOST_REVIEW_ONLY", "SYNTHETIC_OBSERVATION", "NONE");
    }

    public static AudienceDecision ForAudience(PreflightDecision decision, string audience)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!ValidAudiences.Contains(audience, StringComparer.Ordinal))
            throw new ArgumentException($"Invalid audience: {audience}", nameof(audience));

        return new AudienceDecision(
            Schema: Schema,
            Admission: decision.Admission,
            Reason: decision.Reason,
            Effect: decision.Effect,
            Evidence: "SYNTHETIC_ONLY",
            Adapter: "NOT_CONNECTED",
            Audience: audience);
    }

    private static PreflightDecision Deny(string reason) =>
        new("REFUSED", reason, "NONE");
}
