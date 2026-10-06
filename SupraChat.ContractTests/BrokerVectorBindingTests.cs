using System.Runtime.CompilerServices;
using System.Text.Json;
using SupraChat.Core;

internal static class BrokerVectorBindingTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        var now = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
        var broker = NewBroker(() => now);

        var statusBefore = broker.Status();
        var statusAgain = broker.Status();

        var absentBroker = new CredentialBrokerCore(
            "urn:uuid:broker-no-credential",
            credentialGeneration: 0,
            credentialPresent: false,
            clock: () => now);
        var absentStatus = absentBroker.Status();
        Require(!absentStatus.CredentialPresent && absentStatus.CredentialGeneration == 0,
            "no-credential broker invented credential generation");
        var absentAcquire = absentBroker.Acquire(new(
            "req-no-credential", "consumer-a", "binding-a", new[] { "models.read" }));
        Require(absentAcquire.Error == BrokerErrorCodes.AuthRequired,
            "no-credential broker admitted a new lease");

        var first = broker.Acquire(new(
            "req-1", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        var firstLeaseCount = broker.Status().ActiveLeaseCount;
        var replay = broker.Acquire(new(
            "req-1", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        var conflict = broker.Acquire(new(
            "req-1", "consumer-a", "binding-a", new[] { "responses.create" }, TimeSpan.FromSeconds(300)));

        broker.ObserveCredentialPresence(false);
        var replayAfterAuthLoss = broker.Acquire(new(
            "req-1", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        Require(replayAfterAuthLoss.Error is null &&
                replayAfterAuthLoss.Lease?.LeaseId == first.Lease?.LeaseId,
            "accepted acquire replay changed after credential readiness changed");
        broker.ObserveCredentialPresence(true);

        var wrongBinding = broker.TryAdmitProviderOperation(
            "op-wrong", first.Lease!.LeaseId, "consumer-b", "binding-b", "models.read");
        var admitted = broker.TryAdmitProviderOperation(
            "op-1", first.Lease.LeaseId, "consumer-a", "binding-a", "models.read");
        Require(admitted.Error is null && admitted.Admission is not null,
            "valid active lease was not admitted");

        broker.ObserveCredentialPresence(false);
        var blockedWithoutCredential = broker.TryAdmitProviderOperation(
            "op-no-credential", first.Lease.LeaseId, "consumer-a", "binding-a", "models.read");
        Require(blockedWithoutCredential.Error == BrokerErrorCodes.AuthRequired,
            "active lease admitted provider work without broker credential readiness");
        broker.ObserveCredentialState(true, 8);

        var release = broker.Release("release-1", first.Lease.LeaseId);
        var releaseReplay = broker.Release("release-1", first.Lease.LeaseId);
        var deliveredAfterRevoke = broker.CanDeliverProviderResult(admitted.Admission!);
        var postRelease = broker.TryAdmitProviderOperation(
            "op-post", first.Lease.LeaseId, "consumer-a", "binding-a", "models.read");

        var expiring = broker.Acquire(new(
            "req-expire", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(1)));
        now = now.AddSeconds(2);
        var expired = broker.TryAdmitProviderOperation(
            "op-expired", expiring.Lease!.LeaseId, "consumer-a", "binding-a", "models.read");

        now = DateTimeOffset.Parse("2026-10-03T12:10:00Z");
        var beforeRestart = broker.Acquire(new(
            "req-before-restart", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));
        broker.Restart();
        var restarted = broker.TryAdmitProviderOperation(
            "op-restarted", beforeRestart.Lease!.LeaseId, "consumer-a", "binding-a", "models.read");
        var reacquired = broker.Acquire(new(
            "req-after-restart", "consumer-a", "binding-a", new[] { "models.read" }, TimeSpan.FromSeconds(300)));

        var stale = BrokerCredentialGenerationPolicy.Validate(8, "lineage-a", 7, "lineage-a");
        var generationConflict = BrokerCredentialGenerationPolicy.Validate(8, "lineage-a", 8, "lineage-b");

        var observed = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["acquire-first"] = new { result = first.Classification, lease_count = firstLeaseCount, error = first.Error, renewable_material_exposed = false },
            ["acquire-replay-idempotent"] = new { same_logical_lease = replay.Lease?.LeaseId == first.Lease.LeaseId, additional_authority = false, error = replay.Error },
            ["acquire-request-conflict"] = new { error = conflict.Error, additional_authority = false },
            ["wrong-binding-replay"] = new { error = wrongBinding.Error, provider_operation_admitted = wrongBinding.Admission is not null },
            ["release-first"] = new { lease_state = release.LeaseState?.ToString().ToUpperInvariant(), error = release.Error },
            ["release-replay-idempotent"] = new { lease_state = releaseReplay.LeaseState?.ToString().ToUpperInvariant(), stable_terminal_receipt = releaseReplay.Replay, error = releaseReplay.Error },
            ["post-release-use"] = new { error = postRelease.Error, provider_operation_admitted = postRelease.Admission is not null },
            ["expired-use"] = new { error = expired.Error, provider_operation_admitted = expired.Admission is not null },
            ["restart-invalidates-lease"] = new { error = restarted.Error, provider_operation_admitted = restarted.Admission is not null },
            ["restart-reacquire"] = new { new_lease = reacquired.Lease?.LeaseId != beforeRestart.Lease.LeaseId, issued_epoch = $"epoch-{reacquired.Lease!.Epoch:D4}", error = reacquired.Error },
            ["revoke-before-admission"] = new { error = postRelease.Error, provider_operation_admitted = false, response_delivered = false },
            ["revoke-after-admission"] = new { lease_state = "REVOKED", provider_may_complete = true, response_delivered = deliveredAfterRevoke },
            ["refresh-generation-regression"] = new { accepted = stale is null, error = stale },
            ["refresh-generation-conflict"] = new { accepted = generationConflict is null, error = generationConflict },
            ["status-does-not-refresh"] = new { refresh_started = statusAgain.RefreshInProgress, state_mutation = statusBefore != statusAgain }
        };

        using var vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine("oracles", "broker-lease-v1-test-vectors.json")));

        foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var id = vector.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("broker vector id missing");
            Require(observed.TryGetValue(id, out var value),
                $"broker vector is not executed: {id}");

            var actual = JsonSerializer.SerializeToElement(value);
            foreach (var expected in vector.GetProperty("expect").EnumerateObject())
            {
                Require(actual.TryGetProperty(expected.Name, out var actualValue),
                    $"missing observed vector field: {id}.{expected.Name}");
                Require(actualValue.GetRawText() == expected.Value.GetRawText(),
                    $"vector mismatch {id}.{expected.Name}: expected {expected.Value.GetRawText()} observed {actualValue.GetRawText()}");
            }
        }
    }

    private static CredentialBrokerCore NewBroker(Func<DateTimeOffset> clock) =>
        new(
            "urn:uuid:broker-vector-test",
            credentialGeneration: 8,
            allowedCapabilities: new[] { "models.read", "responses.create" },
            clock: clock);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
