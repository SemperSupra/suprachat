using System;
using System.Collections.Generic;
using System.Linq;

namespace SupraChat.Core;

/// <summary>
/// Immutable evaluation result for outbound requests under ChatGPT subscription guardrails.
/// </summary>
public sealed record EntitlementEvaluationResult(
    bool Admitted,
    string Reason,
    string EntitlementTier,
    bool WorkSpendIncurred,
    string EffectiveRoute,
    IReadOnlyList<string> ForbiddenHeadersBlocked,
    bool StorePolicySatisfied,
    string AuditReceiptId);

/// <summary>
/// Enforces fail-closed ChatGPT subscription entitlement guardrails.
/// Guarantees that requests use personal/consumer subscription quota without
/// consuming limited 'Work' pool credits unless explicit operator approval is confirmed.
/// </summary>
public static class EntitlementGuard
{
    public const string Schema = "suprachat-entitlement-guard/v1";
    public const string SubscriptionTier = "SUBSCRIPTION_CHAT";
    public const string WorkTier = "METERED_WORK_CREDITS";
    public const string DirectResponsesRoute = "/v1/responses";
    public const string DirectModelsRoute = "/v1/models";

    public static readonly IReadOnlyList<string> ForbiddenWorkHeaders = new[]
    {
        "OpenAI-Organization-Work-Pool",
        "OpenAI-Work-Task-Runner",
        "OpenAI-Work-Credit-Account",
        "X-OpenAI-Work-Context",
        "X-OpenAI-Organization-Work-Pool"
    };

    public static readonly IReadOnlyList<string> PermittedRoutes = new[]
    {
        DirectResponsesRoute,
        DirectModelsRoute
    };

    /// <summary>
    /// Evaluates whether an outbound request conforms to consumer subscription entitlements.
    /// Fails closed if Work-tier intent is attempted without an explicit, confirmed operator lease,
    /// or if enterprise work-pool headers are present.
    /// </summary>
    public static EntitlementEvaluationResult Evaluate(
        string route,
        string intent,
        bool storeParameter,
        IDictionary<string, string>? headers = null,
        BrowserExperienceModePolicy.HostWorkLease? operatorWorkLease = null,
        long? nowUnixSeconds = null)
    {
        var normalizedRoute = route?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedIntent = intent?.Trim().ToUpperInvariant() ?? "CHAT";
        var auditReceiptId = Guid.NewGuid().ToString("N")[..16];
        var now = nowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. Route validation: Must be an eligible direct subscription endpoint
        if (!PermittedRoutes.Contains(normalizedRoute))
        {
            return new EntitlementEvaluationResult(
                Admitted: false,
                Reason: "REFUSED_ROUTE_NOT_PERMITTED_UNDER_SUBSCRIPTION",
                EntitlementTier: SubscriptionTier,
                WorkSpendIncurred: false,
                EffectiveRoute: normalizedRoute,
                ForbiddenHeadersBlocked: Array.Empty<string>(),
                StorePolicySatisfied: !storeParameter,
                AuditReceiptId: auditReceiptId);
        }

        // 2. Store policy validation: Consumer subscription requires zero cloud retention
        if (storeParameter)
        {
            return new EntitlementEvaluationResult(
                Admitted: false,
                Reason: "REFUSED_STORE_TRUE_FORBIDDEN_UNDER_SUBSCRIPTION",
                EntitlementTier: SubscriptionTier,
                WorkSpendIncurred: false,
                EffectiveRoute: normalizedRoute,
                ForbiddenHeadersBlocked: Array.Empty<string>(),
                StorePolicySatisfied: false,
                AuditReceiptId: auditReceiptId);
        }

        // 3. Header inspection: Strip / reject any work-pool or task-runner billing headers
        var blockedHeaders = new List<string>();
        if (headers != null)
        {
            foreach (var key in headers.Keys.ToList())
            {
                if (ForbiddenWorkHeaders.Any(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase)))
                {
                    blockedHeaders.Add(key);
                    headers.Remove(key); // Strip to sanitize outbound request
                }
            }
        }

        // 4. Intent & Work-Lease verification: Work intent requires explicit operator lease confirmation
        if (normalizedIntent == "WORK")
        {
            if (operatorWorkLease == null ||
                operatorWorkLease.Type != "EXPLICIT_HOST_WORK_LEASE" ||
                operatorWorkLease.Confirmed != true ||
                !operatorWorkLease.Expires.HasValue ||
                operatorWorkLease.Expires.Value <= now)
            {
                return new EntitlementEvaluationResult(
                    Admitted: false,
                    Reason: "REFUSED_WORK_INTENT_REQUIRES_OPERATOR_APPROVAL",
                    EntitlementTier: WorkTier,
                    WorkSpendIncurred: false,
                    EffectiveRoute: normalizedRoute,
                    ForbiddenHeadersBlocked: blockedHeaders,
                    StorePolicySatisfied: true,
                    AuditReceiptId: auditReceiptId);
            }

            // Explicitly confirmed operator lease admitted
            return new EntitlementEvaluationResult(
                Admitted: true,
                Reason: "PERMITTED_WORK_LEASE_OPERATOR_CONFIRMED",
                EntitlementTier: WorkTier,
                WorkSpendIncurred: true,
                EffectiveRoute: normalizedRoute,
                ForbiddenHeadersBlocked: blockedHeaders,
                StorePolicySatisfied: true,
                AuditReceiptId: auditReceiptId);
        }

        // Standard Chat subscription intent admitted
        return new EntitlementEvaluationResult(
            Admitted: true,
            Reason: "PERMITTED_SUBSCRIPTION_CHAT_NO_WORK_SPEND",
            EntitlementTier: SubscriptionTier,
            WorkSpendIncurred: false,
            EffectiveRoute: normalizedRoute,
            ForbiddenHeadersBlocked: blockedHeaders,
            StorePolicySatisfied: true,
            AuditReceiptId: auditReceiptId);
    }

    /// <summary>
    /// Sanitizes an outbound header dictionary by removing any Work-pool billing headers.
    /// </summary>
    public static int SanitizeHeaders(IDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var toRemove = headers.Keys
            .Where(k => ForbiddenWorkHeaders.Any(f => string.Equals(f, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var k in toRemove)
        {
            headers.Remove(k);
        }

        return toRemove.Count;
    }
}
