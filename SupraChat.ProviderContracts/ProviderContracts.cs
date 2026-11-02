namespace SupraChat.ProviderContracts;

// Synthetic RDTE types, not an alternative SupraChat ledger or production provider registry.
// This library contains no network, login, executable plugins or credential material.
public enum ProviderSurface { Api, Web, Agent }
public enum CapabilityState { Unknown, Unsupported, Supported }
public enum ProviderOutcome { Refused, Admitted, Completed, Failed, Incomplete, Unknown, Cancelled }
public enum ProviderEventKind { Delta, Completed, Failed, Incomplete }
public enum UsageSource { Unknown, ProviderReported, LocallyMeasured, Estimated }

public sealed record ProviderBinding(
    string Schema,
    string BindingId,
    string SessionId,
    string ProviderId,
    ProviderSurface Surface,
    string AccountRef,
    string ModelRef,
    string EndpointPolicyRef,
    string CapabilityRevision,
    long AuthGeneration,
    long SessionEpoch,
    long BindingRevision,
    string AdapterVersion);

public sealed record ProviderCapabilities(
    string Schema,
    string ProviderId,
    string Revision,
    IReadOnlyDictionary<string, CapabilityState> Features);

public sealed record ProviderOperation(
    string Schema,
    string OperationId,
    string SessionId,
    string BindingId,
    string ProviderId,
    string AccountRef,
    string ModelRef,
    string EndpointPolicyRef,
    string CapabilityRevision,
    long AuthGeneration,
    long SessionEpoch,
    long BindingRevision,
    string AdapterVersion,
    string RequiredCapability,
    string RequestDigest);

public sealed record ProviderDecision(bool Admitted, string Code)
{
    public static ProviderDecision Refuse(string code) => new(false, code);
    public static ProviderDecision Admit() => new(true, "ADMITTED");
}

public sealed record ProviderEvent(
    string OperationId, long Sequence, ProviderEventKind Kind);

public sealed record ProviderUsage(
    UsageSource Source,
    long? InputTokens,
    long? OutputTokens,
    decimal? Cost,
    string? Currency);

public static class ProviderAdmission
{
    public const string BindingSchema = "suprachat-provider-binding/v1";
    public const string CapabilitiesSchema = "suprachat-provider-capabilities/v1";
    public const string OperationSchema = "suprachat-provider-operation/v1";

    // Host-owned policy; adapter claims are insufficient for authorization.
    public static ProviderDecision Evaluate(
        ProviderBinding binding, ProviderCapabilities capabilities, ProviderOperation operation)
    {
        if (binding.Schema != BindingSchema ||
            capabilities.Schema != CapabilitiesSchema ||
            operation.Schema != OperationSchema)
            return ProviderDecision.Refuse("SCHEMA_UNSUPPORTED");

        if (string.IsNullOrWhiteSpace(binding.BindingId) ||
            string.IsNullOrWhiteSpace(binding.SessionId) ||
            string.IsNullOrWhiteSpace(binding.ProviderId) ||
            string.IsNullOrWhiteSpace(binding.AccountRef) ||
            string.IsNullOrWhiteSpace(binding.ModelRef) ||
            string.IsNullOrWhiteSpace(binding.EndpointPolicyRef) ||
            string.IsNullOrWhiteSpace(binding.CapabilityRevision) ||
            string.IsNullOrWhiteSpace(binding.AdapterVersion) ||
            string.IsNullOrWhiteSpace(operation.OperationId))
            return ProviderDecision.Refuse("INVALID_BINDING");

        if (binding.AuthGeneration <= 0 || binding.SessionEpoch <= 0 ||
            binding.BindingRevision <= 0)
            return ProviderDecision.Refuse("INVALID_GENERATION");

        if (!Enum.IsDefined(binding.Surface))
            return ProviderDecision.Refuse("INVALID_SURFACE");

        // These refs are *opaque aliases*, never emails, token strings or URLs.
        if (!IsOpaqueAlias(binding.AccountRef) || !IsOpaqueAlias(binding.EndpointPolicyRef))
            return ProviderDecision.Refuse("UNSAFE_BINDING_ALIAS");

        if (capabilities.Features is null)
            return ProviderDecision.Refuse("INVALID_CAPABILITIES");

        if (operation.SessionId != binding.SessionId ||
            operation.BindingId != binding.BindingId ||
            operation.ProviderId != binding.ProviderId ||
            operation.AccountRef != binding.AccountRef)
            return ProviderDecision.Refuse("IDENTITY_OR_SESSION_MISMATCH");

        if (operation.AuthGeneration != binding.AuthGeneration ||
            operation.SessionEpoch != binding.SessionEpoch ||
            operation.BindingRevision != binding.BindingRevision)
            return ProviderDecision.Refuse("STALE_GENERATION");

        if (operation.ModelRef != binding.ModelRef ||
            operation.EndpointPolicyRef != binding.EndpointPolicyRef ||
            operation.CapabilityRevision != binding.CapabilityRevision ||
            operation.AdapterVersion != binding.AdapterVersion ||
            capabilities.ProviderId != binding.ProviderId ||
            capabilities.Revision != binding.CapabilityRevision)
            return ProviderDecision.Refuse("BINDING_CHANGED");

        if (!IsSha256Hex(operation.RequestDigest))
            return ProviderDecision.Refuse("INVALID_REQUEST_DIGEST");

        if (string.IsNullOrWhiteSpace(operation.RequiredCapability) ||
            !capabilities.Features.TryGetValue(operation.RequiredCapability, out var support) ||
            support != CapabilityState.Supported)
            return ProviderDecision.Refuse("CAPABILITY_UNAVAILABLE");

        return ProviderDecision.Admit();
    }

    // A replay is safe to recognize only when the entire immutable admission matches.
    // The actual idempotency ledger belongs to the existing SupraChat session authority.
    public static string CompareReplay(ProviderOperation prior, ProviderOperation candidate)
    {
        if (prior.OperationId != candidate.OperationId)
            return "DISTINCT_OPERATION";
        return prior == candidate ? "IDENTICAL_REPLAY" : "REPLAY_CONFLICT";
    }

    private static bool IsOpaqueAlias(string? value)
    {
        if (value is null || value.Length is < 1 or > 64)
            return false;
        foreach (var ch in value)
            if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
                return false;
        return true;
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var ch in value)
            if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        return true;
    }
}

public static class ProviderStreamReconciler
{
    // Deliberately conservative: completion requires an exact terminal event and
    // clean, ordered, closed synthetic stream. No partial stream is a completion receipt.
    public static ProviderOutcome Classify(
        string operationId, IReadOnlyList<ProviderEvent> events, bool transportClosed)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return ProviderOutcome.Unknown;
        long previous = -1;
        ProviderEventKind? terminal = null;
        foreach (var ev in events)
        {
            if (ev.OperationId != operationId || ev.Sequence <= previous || terminal is not null)
                return ProviderOutcome.Unknown;
            previous = ev.Sequence;
            if (ev.Kind != ProviderEventKind.Delta)
                terminal = ev.Kind;
        }
        if (!transportClosed || terminal is null) return ProviderOutcome.Unknown;
        return terminal switch
        {
            ProviderEventKind.Completed => ProviderOutcome.Completed,
            ProviderEventKind.Failed => ProviderOutcome.Failed,
            ProviderEventKind.Incomplete => ProviderOutcome.Incomplete,
            _ => ProviderOutcome.Unknown
        };
    }
}

public static class ProviderUsageValidation
{
    // "Unknown" is not a zero usage/billing estimate.
    public static bool Valid(ProviderUsage usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.Cost < 0)
            return false;
        if (usage.Source == UsageSource.Unknown)
            return usage.InputTokens is null && usage.OutputTokens is null &&
                   usage.Cost is null && usage.Currency is null;
        if (usage.Cost is not null && string.IsNullOrWhiteSpace(usage.Currency))
            return false;
        return true;
    }
}
