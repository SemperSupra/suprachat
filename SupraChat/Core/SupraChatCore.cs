namespace SupraChat.Core;

public sealed record SupraChatAuthStatus(
    string Schema,
    bool Present,
    bool PlanUsage,
    DateTimeOffset? ExpiresAt,
    int ScopeCount,
    long CredentialGeneration,
    string? ClientIdSuffix,
    string? SubjectFingerprint,
    bool EarliestRefreshMetadataPresent);

public sealed record CredentialBundleReceipt(
    string Schema,
    string Operation,
    string Path,
    string Mode,
    DateTimeOffset Timestamp,
    string ClientIdSuffix,
    string SubjectFingerprint,
    long CredentialGeneration,
    bool ContainsTokenMaterial);

public sealed class SupraChatCore
{
    public const string Schema = "suprachat-core/v1";

    private readonly SiwcClient _siwc;
    private readonly ResponsesClient _responses;

    public SupraChatCore(
        SiwcClient? siwc = null,
        ResponsesClient? responses = null)
    {
        _siwc = siwc ?? new SiwcClient();
        _responses = responses ?? new ResponsesClient();
    }

    public async Task<SupraChatAuthStatus> GetAuthStatusAsync()
    {
        var credential = await CredentialStore.TryLoadAsync().ConfigureAwait(false);
        return DescribeCredential(credential);
    }

    public async Task<SiwcCredential> GetUsableCredentialAsync(
        bool requirePlanUsage = true,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await CredentialStore
            .AcquireExclusiveCredentialLeaseAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var credential = await CredentialStore.TryLoadAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No local ChatGPT authorization is available. Complete Continue with ChatGPT in the SupraChat GUI first.");

        var refreshed = await _siwc
            .RefreshIfNeededAsync(credential, correlationId: correlationId)
            .ConfigureAwait(false);

        if (!ReferenceEquals(refreshed, credential))
            await CredentialStore.SaveAsync(refreshed).ConfigureAwait(false);

        if (requirePlanUsage && !refreshed.HasPlanUsage)
            throw new InvalidOperationException(
                $"Saved authorization does not include {SiwcProtocol.RequiredPlanScope}. Enable plan usage in the SupraChat GUI.");

        return refreshed;
    }

    public async Task<SiwcCredential> CommitInteractiveCredentialAsync(
        SiwcCredential candidate,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await CredentialStore
            .AcquireExclusiveCredentialLeaseAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var current = await CredentialStore.TryLoadAsync().ConfigureAwait(false);
        if (current is not null &&
            string.Equals(current.ClientId, candidate.ClientId, StringComparison.Ordinal) &&
            string.Equals(current.Subject, candidate.Subject, StringComparison.Ordinal))
        {
            candidate = candidate with
            {
                Generation = Math.Max(
                    Math.Max(1, candidate.Generation),
                    Math.Max(1, current.Generation) + 1)
            };
        }
        else
        {
            candidate = candidate with { Generation = Math.Max(1, candidate.Generation) };
        }

        await CredentialStore.SaveAsync(candidate).ConfigureAwait(false);
        return candidate;
    }

    public async Task<IReadOnlyList<ModelChoice>> ListModelsAsync(
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var credential = await GetUsableCredentialAsync(
            requirePlanUsage: true,
            correlationId: correlationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _responses.ListModelsAsync(credential.AccessToken).ConfigureAwait(false);
    }

    public async Task<CredentialBundleReceipt> ExportCredentialAsync(
        string outputPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await CredentialStore
            .AcquireExclusiveCredentialLeaseAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var credential = await CredentialStore.TryLoadAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("No renewable SIWC credential is available to export.");

        await PortableCredentialBundle.WriteAsync(
            outputPath,
            credential,
            passphrase,
            PortableCredentialBundle.CopyMode).ConfigureAwait(false);

        var envelope = await PortableCredentialBundle.ReadEnvelopeAsync(outputPath).ConfigureAwait(false);
        var summary = PortableCredentialBundle.Inspect(envelope);
        return Receipt("export", outputPath, summary, credential.Generation);
    }

    public async Task<CredentialBundleReceipt> ImportCredentialAsync(
        string inputPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await CredentialStore
            .AcquireExclusiveCredentialLeaseAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var envelope = await PortableCredentialBundle.ReadEnvelopeAsync(inputPath).ConfigureAwait(false);
        var imported = PortableCredentialBundle.Unprotect(envelope, passphrase);

        // Host identity belongs to the runtime receiving the transferred/copy credential.
        // Do not overwrite this host with the source host embedded in the portable payload.
        var localHostId = await HostIdentity.LoadOrCreateAsync().ConfigureAwait(false);
        imported = imported with { HostId = localHostId };

        await CredentialStore.SaveAsync(imported).ConfigureAwait(false);
        var summary = PortableCredentialBundle.Inspect(envelope);
        return Receipt("import", inputPath, summary, imported.Generation);
    }

    public async Task<PortableCredentialBundleSummary> InspectCredentialBundleAsync(string path)
    {
        var envelope = await PortableCredentialBundle.ReadEnvelopeAsync(path).ConfigureAwait(false);
        return PortableCredentialBundle.Inspect(envelope);
    }

    public static SupraChatAuthStatus DescribeCredential(SiwcCredential? credential) =>
        credential is null
            ? new(
                Schema,
                Present: false,
                PlanUsage: false,
                ExpiresAt: null,
                ScopeCount: 0,
                CredentialGeneration: 0,
                ClientIdSuffix: null,
                SubjectFingerprint: null,
                EarliestRefreshMetadataPresent: false)
            : new(
                Schema,
                Present: true,
                PlanUsage: credential.HasPlanUsage,
                ExpiresAt: credential.ExpiresAt,
                ScopeCount: credential.Scopes.Length,
                CredentialGeneration: Math.Max(1, credential.Generation),
                ClientIdSuffix: Suffix(credential.ClientId),
                SubjectFingerprint: Fingerprint(credential.Subject),
                EarliestRefreshMetadataPresent: !string.IsNullOrWhiteSpace(credential.EarliestRefreshAtRaw));

    private static CredentialBundleReceipt Receipt(
        string operation,
        string path,
        PortableCredentialBundleSummary summary,
        long generation) =>
        new(
            "suprachat-siwc-credential-bundle-receipt/v1",
            operation,
            Path.GetFullPath(path),
            summary.Mode,
            DateTimeOffset.UtcNow,
            summary.ClientIdSuffix,
            summary.SubjectFingerprint,
            Math.Max(1, generation),
            ContainsTokenMaterial: false);

    private static string Suffix(string value) =>
        value[^Math.Min(value.Length, 12)..];

    private static string Fingerprint(string subject)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(subject));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
