using System.Text.Json;
using SupraChat.Core;

internal static class BrokerContractTests
{
    public static void Run()
    {
        var now = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
        var broker = new CredentialBrokerCore(
            "urn:uuid:broker-test",
            credentialGeneration: 8,
            allowedCapabilities: new[] { "models.read", "responses.create" },
            clock: () => now);

        var statusBefore = broker.Status();
        var statusAgain = broker.Status();
        Require(statusBefore == statusAgain, "broker/status must be read-only and stable");

        var first = broker.Acquire(new BrokerAcquireRequest(
            "req-1", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        Require(first.Error is null && first.Lease is not null, "first acquire failed");
        Require(first.Classification == "LEASED", "first acquire classification changed");
        Require(!first.Replay, "first acquire cannot be replay");

        var replay = broker.Acquire(new BrokerAcquireRequest(
            "req-1", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        Require(replay.Error is null && replay.Replay, "acquire replay must be idempotent");
        Require(replay.Lease!.LeaseId == first.Lease!.LeaseId, "acquire replay minted another lease");

        var conflict = broker.Acquire(new BrokerAcquireRequest(
            "req-1", "consumer-a", "binding-a", new[] { "responses.create" }, TimeSpan.FromSeconds(300)));
        Require(conflict.Error == BrokerErrorCodes.RequestConflict, "conflicting acquire replay was not rejected");

        var wrongBinding = broker.TryAdmitProviderOperation(
            "op-wrong-binding", first.Lease.LeaseId, "consumer-b", "binding-b", "models.read");
        Require(wrongBinding.Error == BrokerErrorCodes.ConsumerBindingFailed, "wrong consumer binding was admitted");

        var admission = broker.TryAdmitProviderOperation(
            "op-1", first.Lease.LeaseId, "consumer-a", "binding-a", "models.read");
        Require(admission.Error is null && admission.Admission is not null, "valid lease use was not admitted");

        var release = broker.Release("release-1", first.Lease.LeaseId);
        Require(release.Error is null && release.LeaseState == BrokerLeaseState.Revoked, "release failed");
        var releaseReplay = broker.Release("release-1", first.Lease.LeaseId);
        Require(releaseReplay.Error is null && releaseReplay.Replay, "release replay must be idempotent");
        Require(!broker.CanDeliverProviderResult(admission.Admission!), "post-revoke admitted result must be discarded");

        var postRelease = broker.TryAdmitProviderOperation(
            "op-post-release", first.Lease.LeaseId, "consumer-a", "binding-a", "models.read");
        Require(postRelease.Error == BrokerErrorCodes.LeaseRevoked, "revoked lease admitted new work");

        var expiring = broker.Acquire(new BrokerAcquireRequest(
            "req-expire", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(1)));
        Require(expiring.Error is null, "expiry fixture acquire failed");
        now = now.AddSeconds(2);
        var expired = broker.TryAdmitProviderOperation(
            "op-expired", expiring.Lease!.LeaseId, "consumer-a", "binding-a", "models.read");
        Require(expired.Error == BrokerErrorCodes.LeaseExpired, "expired lease admitted work");

        now = DateTimeOffset.Parse("2026-10-03T12:10:00Z");
        var restartLease = broker.Acquire(new BrokerAcquireRequest(
            "req-before-restart", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        var priorEpoch = restartLease.Lease!.Epoch;
        broker.Restart();
        var restarted = broker.TryAdmitProviderOperation(
            "op-after-restart", restartLease.Lease.LeaseId, "consumer-a", "binding-a", "models.read");
        Require(restarted.Error == BrokerErrorCodes.BrokerRestarted, "pre-restart lease survived broker epoch change");
        var reacquired = broker.Acquire(new BrokerAcquireRequest(
            "req-after-restart", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        Require(reacquired.Error is null && reacquired.Lease!.Epoch > priorEpoch, "restart reacquire did not use new epoch");

        var capDenied = broker.TryAdmitProviderOperation(
            "op-cap-denied", reacquired.Lease.LeaseId, "consumer-a", "binding-a", "responses.create");
        Require(capDenied.Error == BrokerErrorCodes.CapabilityDenied, "lease authority widened beyond granted capability");

        Require(
            BrokerCredentialGenerationPolicy.Validate(8, "lineage-a", 7, "lineage-a") ==
            BrokerErrorCodes.StaleGeneration,
            "credential generation regression was not rejected");
        Require(
            BrokerCredentialGenerationPolicy.Validate(8, "lineage-a", 8, "lineage-b") ==
            BrokerErrorCodes.GenerationConflict,
            "same-generation refresh-lineage conflict was not rejected");
        Require(
            BrokerCredentialGenerationPolicy.Validate(8, "lineage-a", 9, "lineage-b") is null,
            "newer credential generation was incorrectly rejected");

        var safeJson = broker.ToPrivacySafeJson(reacquired);
        Require(!safeJson.Contains("ACCESS_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal),
            "broker receipt leaked access token sentinel");
        Require(!safeJson.Contains("REFRESH_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal),
            "broker receipt leaked refresh token sentinel");

        var vectorsPath = Path.Combine("oracles", "broker-lease-v1-test-vectors.json");
        Require(File.Exists(vectorsPath), "broker v1 public-safe vectors missing");
        using var vectors = JsonDocument.Parse(File.ReadAllText(vectorsPath));
        Require(
            vectors.RootElement.GetProperty("schema").GetString() == "suprachat-broker-lease-test-vectors/v1",
            "broker vector schema drifted");
        var ids = vectors.RootElement.GetProperty("vectors")
            .EnumerateArray()
            .Select(x => x.GetProperty("id").GetString())
            .Where(x => x is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "acquire-first",
                     "acquire-replay-idempotent",
                     "acquire-request-conflict",
                     "wrong-binding-replay",
                     "release-first",
                     "release-replay-idempotent",
                     "post-release-use",
                     "expired-use",
                     "restart-invalidates-lease",
                     "restart-reacquire",
                     "revoke-before-admission",
                     "revoke-after-admission",
                     "refresh-generation-regression",
                     "refresh-generation-conflict",
                     "status-does-not-refresh"
                 })
        {
            Require(ids.Contains(required), $"broker vector missing: {required}");
        }

        var resultJson = JsonSerializer.Serialize(new { first, replay, release, status = broker.Status() });
        foreach (var forbidden in new[] { "access_token", "refresh_token", "id_token", "authorization_code", "cookie" })
            Require(!resultJson.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"broker serialized result leaked forbidden credential field: {forbidden}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
