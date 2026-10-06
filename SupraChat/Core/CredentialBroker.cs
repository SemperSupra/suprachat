using System.Text.Json;

namespace SupraChat.Core;

public static class BrokerErrorCodes
{
    public const string AuthRequired = "AUTH_REQUIRED";
    public const string CapabilityDenied = "CAPABILITY_DENIED";
    public const string LeaseExpired = "LEASE_EXPIRED";
    public const string LeaseRevoked = "LEASE_REVOKED";
    public const string LeaseNotFound = "LEASE_NOT_FOUND";
    public const string RequestConflict = "REQUEST_CONFLICT";
    public const string ConsumerBindingFailed = "CONSUMER_BINDING_FAILED";
    public const string BrokerRestarted = "BROKER_RESTARTED";
    public const string StaleGeneration = "STALE_GENERATION";
    public const string GenerationConflict = "GENERATION_CONFLICT";
}

public enum BrokerLeaseState
{
    Active,
    Revoked
}

public sealed record BrokerStatus(
    string HostId,
    long Epoch,
    long CredentialGeneration,
    bool CredentialPresent,
    bool RefreshInProgress,
    int ActiveLeaseCount);

public sealed record BrokerLeaseView(
    string LeaseId,
    string ConsumerId,
    string ConsumerBinding,
    string[] Capabilities,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    long Epoch,
    long CredentialGeneration,
    BrokerLeaseState State);

public sealed record BrokerAcquireRequest(
    string RequestId,
    string ConsumerId,
    string ConsumerBinding,
    IReadOnlyList<string> Capabilities,
    TimeSpan? Ttl = null);

public sealed record BrokerAcquireResult(
    string RequestId,
    string Classification,
    BrokerLeaseView? Lease,
    string? Error,
    bool Replay);

public sealed record BrokerReleaseResult(
    string RequestId,
    string Classification,
    string LeaseId,
    BrokerLeaseState? LeaseState,
    string? Error,
    bool Replay);

public sealed record BrokerAdmission(
    string OperationId,
    string LeaseId,
    string ConsumerId,
    string ConsumerBinding,
    string Capability,
    long Epoch,
    DateTimeOffset AdmittedAt);

public sealed record BrokerAdmissionResult(
    BrokerAdmission? Admission,
    string? Error);

public static class BrokerCredentialGenerationPolicy
{
    public static string? Validate(
        long currentGeneration,
        string currentLineage,
        long candidateGeneration,
        string candidateLineage)
    {
        if (candidateGeneration < currentGeneration)
            return BrokerErrorCodes.StaleGeneration;

        if (candidateGeneration == currentGeneration &&
            !string.Equals(currentLineage, candidateLineage, StringComparison.Ordinal))
            return BrokerErrorCodes.GenerationConflict;

        return null;
    }
}

public sealed class CredentialBrokerCore
{
    private sealed record AcquireRecord(string Fingerprint, string LeaseId);
    private sealed record ReleaseRecord(string LeaseId, BrokerReleaseResult Result);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _defaultTtl;
    private readonly TimeSpan _maxTtl;
    private readonly HashSet<string> _allowedCapabilities;
    private readonly Dictionary<string, BrokerLeaseView> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AcquireRecord> _acquireRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReleaseRecord> _releaseRequests = new(StringComparer.Ordinal);

    private long _epoch;
    private long _credentialGeneration;
    private bool _credentialPresent;
    private int _leaseSequence;

    public CredentialBrokerCore(
        string hostId,
        long credentialGeneration,
        bool credentialPresent = true,
        long epoch = 1,
        IEnumerable<string>? allowedCapabilities = null,
        TimeSpan? defaultTtl = null,
        TimeSpan? maxTtl = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Broker host identity is required.", nameof(hostId));
        if (credentialGeneration < 0 || (credentialPresent && credentialGeneration < 1))
            throw new ArgumentOutOfRangeException(nameof(credentialGeneration));
        if (epoch < 1)
            throw new ArgumentOutOfRangeException(nameof(epoch));

        HostId = hostId;
        _credentialGeneration = credentialGeneration;
        _credentialPresent = credentialPresent;
        _epoch = epoch;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
        _maxTtl = maxTtl ?? TimeSpan.FromMinutes(15);
        _allowedCapabilities = new HashSet<string>(
            allowedCapabilities ?? new[] { "models.read", "responses.create" },
            StringComparer.Ordinal);

        if (_defaultTtl <= TimeSpan.Zero || _defaultTtl > _maxTtl)
            throw new ArgumentOutOfRangeException(nameof(defaultTtl));
    }

    public string HostId { get; }

    public BrokerStatus Status()
    {
        lock (_gate)
        {
            var now = _clock();
            var active = _leases.Values.Count(x =>
                x.State == BrokerLeaseState.Active &&
                x.Epoch == _epoch &&
                x.ExpiresAt > now);

            return new BrokerStatus(
                HostId,
                _epoch,
                _credentialGeneration,
                _credentialPresent,
                RefreshInProgress: false,
                ActiveLeaseCount: active);
        
        }
    }

    public BrokerAcquireResult Acquire(BrokerAcquireRequest request)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(request);

            var capabilities = request.Capabilities
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            if (string.IsNullOrWhiteSpace(request.RequestId) ||
                string.IsNullOrWhiteSpace(request.ConsumerId) ||
                string.IsNullOrWhiteSpace(request.ConsumerBinding) ||
                capabilities.Length == 0 ||
                capabilities.Any(x => !_allowedCapabilities.Contains(x)))
                return AcquireError(request.RequestId, BrokerErrorCodes.CapabilityDenied);

            var ttl = request.Ttl ?? _defaultTtl;
            if (ttl <= TimeSpan.Zero || ttl > _maxTtl)
                return AcquireError(request.RequestId, BrokerErrorCodes.CapabilityDenied);

            var fingerprint = string.Join(
                "|",
                request.ConsumerId,
                request.ConsumerBinding,
                ttl.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(",", capabilities));

            // Once an acquire request is accepted, an exact replay must return the
            // same logical lease even if current credential readiness later changes.
            if (_acquireRequests.TryGetValue(request.RequestId, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return AcquireError(request.RequestId, BrokerErrorCodes.RequestConflict);

                return new BrokerAcquireResult(
                    request.RequestId,
                    "LEASED",
                    _leases[existing.LeaseId],
                    Error: null,
                    Replay: true);
            }

            if (!_credentialPresent)
                return AcquireError(request.RequestId, BrokerErrorCodes.AuthRequired);

            var now = _clock();
            var leaseId = $"lease-{++_leaseSequence:D6}";
            var lease = new BrokerLeaseView(
                leaseId,
                request.ConsumerId,
                request.ConsumerBinding,
                capabilities,
                now,
                now.Add(ttl),
                _epoch,
                _credentialGeneration,
                BrokerLeaseState.Active);

            _leases.Add(leaseId, lease);
            _acquireRequests.Add(request.RequestId, new AcquireRecord(fingerprint, leaseId));

            return new BrokerAcquireResult(
                request.RequestId,
                "LEASED",
                lease,
                Error: null,
                Replay: false);
        
        }
    }

    public BrokerLeaseView? LeaseStatus(string leaseId)
    {
        lock (_gate)
            return _leases.TryGetValue(leaseId, out var lease) ? lease : null;
    }

    public BrokerReleaseResult Release(string requestId, string leaseId)
    {
        lock (_gate)
        {
            if (_releaseRequests.TryGetValue(requestId, out var prior))
            {
                if (!string.Equals(prior.LeaseId, leaseId, StringComparison.Ordinal))
                    return ReleaseError(requestId, leaseId, BrokerErrorCodes.RequestConflict);

                return prior.Result with { Replay = true };
            }

            if (!_leases.TryGetValue(leaseId, out var lease))
                return ReleaseError(requestId, leaseId, BrokerErrorCodes.LeaseNotFound);

            var revoked = lease with { State = BrokerLeaseState.Revoked };
            _leases[leaseId] = revoked;

            var result = new BrokerReleaseResult(
                requestId,
                "REVOKED",
                leaseId,
                BrokerLeaseState.Revoked,
                Error: null,
                Replay: false);
            _releaseRequests[requestId] = new ReleaseRecord(leaseId, result);
            return result;
        
        }
    }

    public BrokerAdmissionResult TryAdmitProviderOperation(
        string operationId,
        string leaseId,
        string consumerId,
        string consumerBinding,
        string capability)
    {
        lock (_gate)
        {
            var error = ValidateLeaseUse(leaseId, consumerId, consumerBinding, capability);
            if (error is not null)
                return new BrokerAdmissionResult(null, error);

            return new BrokerAdmissionResult(
                new BrokerAdmission(
                    operationId,
                    leaseId,
                    consumerId,
                    consumerBinding,
                    capability,
                    _epoch,
                    _clock()),
                Error: null);
        
        }
    }

    public bool CanDeliverProviderResult(BrokerAdmission admission)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(admission);

            if (!_leases.TryGetValue(admission.LeaseId, out var lease))
                return false;

            return admission.Epoch == _epoch &&
                   lease.Epoch == _epoch &&
                   lease.State == BrokerLeaseState.Active &&
                   lease.ExpiresAt > _clock();
        
        }
    }

    public void Restart()
    {
        lock (_gate)
        {
            checked { _epoch++; }
        }
    }

    // Observation input from the owning credential subsystem. This does not
    // create, refresh, persist, or transfer renewable credentials. Generation
    // validity remains the CredentialStore/SiwcClient owner's responsibility.
    public void ObserveCredentialState(bool present, long generation)
    {
        if (generation < 0 || (present && generation < 1))
            throw new ArgumentOutOfRangeException(nameof(generation));

        lock (_gate)
        {
            _credentialPresent = present;
            _credentialGeneration = present ? generation : 0;
        }
    }

    public void ObserveCredentialPresence(bool present)
    {
        lock (_gate)
            _credentialPresent = present;
    }

    public string ToPrivacySafeJson(object value)
    {
        var json = JsonSerializer.Serialize(value);
        foreach (var forbidden in new[]
                 {
                     "access_token",
                     "refresh_token",
                     "id_token",
                     "authorization_code",
                     "cookie"
                 })
        {
            if (json.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Broker result contains forbidden credential field: {forbidden}");
        }
        return json;
    }

    private string? ValidateLeaseUse(
        string leaseId,
        string consumerId,
        string consumerBinding,
        string capability)
    {
        if (!_leases.TryGetValue(leaseId, out var lease))
            return BrokerErrorCodes.LeaseNotFound;
        if (lease.Epoch != _epoch)
            return BrokerErrorCodes.BrokerRestarted;
        if (lease.State == BrokerLeaseState.Revoked)
            return BrokerErrorCodes.LeaseRevoked;
        if (lease.ExpiresAt <= _clock())
            return BrokerErrorCodes.LeaseExpired;
        if (!string.Equals(lease.ConsumerId, consumerId, StringComparison.Ordinal) ||
            !string.Equals(lease.ConsumerBinding, consumerBinding, StringComparison.Ordinal))
            return BrokerErrorCodes.ConsumerBindingFailed;
        if (!lease.Capabilities.Contains(capability, StringComparer.Ordinal))
            return BrokerErrorCodes.CapabilityDenied;
        if (!_credentialPresent)
            return BrokerErrorCodes.AuthRequired;
        return null;
    }

    private static BrokerAcquireResult AcquireError(string requestId, string error) =>
        new(requestId, "REJECTED", null, error, Replay: false);

    private static BrokerReleaseResult ReleaseError(string requestId, string leaseId, string error) =>
        new(requestId, "REJECTED", leaseId, null, error, Replay: false);
}
