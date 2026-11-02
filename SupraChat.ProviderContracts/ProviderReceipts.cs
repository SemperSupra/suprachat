namespace SupraChat.ProviderContracts;

// Settled, metadata-only receipt. No prompt, completion, credential, URL or header fields.
public enum ProviderFailureClass
{
    Auth, Capability, RateLimit, Quota, Transport, Policy, Incomplete, Unknown
}

public enum SideEffectCertainty { None, Possible, Confirmed }

public sealed record ProviderReceipt(
    string Schema,
    string OperationId,
    string BindingId,
    ProviderOutcome Outcome,
    ProviderFailureClass? FailureClass,
    UsageSource UsageSource,
    long? InputTokens,
    long? OutputTokens,
    long? DurationMs,
    long? FirstChunkMs,
    string? ProviderRequestAlias,
    bool ContainsContent);

public sealed record ProviderRetryFacts(
    bool Transient, bool Submitted, bool StreamingStarted, SideEffectCertainty SideEffects);

public static class ProviderRetryPolicy
{
    // A retry is eligible only while nothing could have reached the remote provider.
    // Eligibility is not authorization: the SupraChat host still owns admission.
    public static bool Eligible(ProviderRetryFacts facts) =>
        facts.Transient && !facts.Submitted && !facts.StreamingStarted &&
        facts.SideEffects == SideEffectCertainty.None;
}

public static class ProviderReceiptPolicy
{
    public const string Schema = "suprachat-provider-receipt/v1";

    public static bool Valid(ProviderReceipt receipt)
    {
        if (receipt.Schema != Schema ||
            string.IsNullOrWhiteSpace(receipt.OperationId) ||
            string.IsNullOrWhiteSpace(receipt.BindingId) ||
            receipt.ContainsContent ||
            receipt.Outcome == ProviderOutcome.Admitted ||
            receipt.InputTokens < 0 || receipt.OutputTokens < 0 ||
            receipt.DurationMs < 0 || receipt.FirstChunkMs < 0)
            return false;

        if ((receipt.FirstChunkMs is not null && receipt.DurationMs is null) ||
            (receipt.FirstChunkMs is { } first && receipt.DurationMs is { } total && first > total))
            return false;

        if (receipt.Outcome == ProviderOutcome.Completed && receipt.FailureClass is not null)
            return false;
        if (receipt.Outcome != ProviderOutcome.Completed && receipt.FailureClass is null)
            return false;

        if (receipt.UsageSource == UsageSource.Unknown &&
            (receipt.InputTokens is not null || receipt.OutputTokens is not null))
            return false;

        // Request IDs must already be privacy-safe aliases, not raw vendor headers.
        if (receipt.ProviderRequestAlias is { } alias)
        {
            if (alias.Length is < 1 or > 32) return false;
            foreach (var c in alias)
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
                    return false;
        }
        return true;
    }
}
