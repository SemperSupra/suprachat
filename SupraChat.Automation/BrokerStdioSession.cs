using SupraChat.Core;

namespace SupraChat.Automation;

internal sealed class BrokerStdioSession
{
    private const string ConsumerId = "suprachat-automation-stdio";

    private readonly SupraChatCore _core;
    private readonly string _consumerBinding = "stdio-" + Guid.NewGuid().ToString("N");
    private CredentialBrokerCore? _broker;
    private string? _registrationSuffix;

    public BrokerStdioSession(SupraChatCore core)
    {
        _core = core;
    }

    public async Task<BrokerStatus> StatusAsync()
    {
        var broker = await ObserveAsync(requireUsableCredential: false).ConfigureAwait(false);
        return broker.Status();
    }

    public async Task<BrokerAcquireResult> AcquireAsync(
        string requestId,
        IReadOnlyList<string> capabilities,
        TimeSpan? ttl)
    {
        CredentialBrokerCore broker;
        try
        {
            broker = await ObserveAsync(requireUsableCredential: true).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            broker = await ObserveAsync(requireUsableCredential: false).ConfigureAwait(false);
            return new BrokerAcquireResult(
                requestId,
                "REJECTED",
                Lease: null,
                Error: BrokerErrorCodes.AuthRequired,
                Replay: false);
        }

        return broker.Acquire(new BrokerAcquireRequest(
            requestId,
            ConsumerId,
            _consumerBinding,
            capabilities,
            ttl));
    }

    public async Task<BrokerLeaseView?> LeaseStatusAsync(string leaseId)
    {
        var broker = await ObserveAsync(requireUsableCredential: false).ConfigureAwait(false);
        return broker.LeaseStatus(leaseId);
    }

    public async Task<BrokerReleaseResult> ReleaseAsync(string requestId, string leaseId)
    {
        var broker = await ObserveAsync(requireUsableCredential: false).ConfigureAwait(false);
        return broker.Release(requestId, leaseId);
    }

    public async Task<BrokerAdmissionResult> AdmitAsync(
        string operationId,
        string leaseId,
        string capability)
    {
        var broker = await ObserveAsync(requireUsableCredential: false).ConfigureAwait(false);
        return broker.TryAdmitProviderOperation(
            operationId,
            leaseId,
            ConsumerId,
            _consumerBinding,
            capability);
    }

    public async Task<string?> ResultDeliveryErrorAsync(BrokerAdmission admission)
    {
        var broker = await ObserveAsync(requireUsableCredential: false).ConfigureAwait(false);
        if (broker.CanDeliverProviderResult(admission))
            return null;

        var check = broker.TryAdmitProviderOperation(
            admission.OperationId + "-delivery-check",
            admission.LeaseId,
            ConsumerId,
            _consumerBinding,
            admission.Capability);
        return check.Error ?? BrokerErrorCodes.LeaseRevoked;
    }

    public async Task RefreshObservationAsync()
    {
        _ = await ObserveAsync(requireUsableCredential: false).ConfigureAwait(false);
    }

    private async Task<CredentialBrokerCore> ObserveAsync(bool requireUsableCredential)
    {
        SupraChatAuthStatus status;
        if (requireUsableCredential)
        {
            var credential = await _core
                .GetUsableCredentialAsync(requirePlanUsage: true)
                .ConfigureAwait(false);
            status = SupraChatCore.DescribeCredential(credential);
        }
        else
        {
            status = await _core.GetAuthStatusAsync().ConfigureAwait(false);
        }

        if (_broker is null)
        {
            var hostId = await HostIdentity.LoadOrCreateAsync().ConfigureAwait(false);
            _broker = new CredentialBrokerCore(
                hostId,
                credentialGeneration: status.Present ? status.CredentialGeneration : 0,
                credentialPresent: status.Present,
                allowedCapabilities: new[] { "models.read", "responses.create" });
            _registrationSuffix = status.ClientIdSuffix;
        }
        else
        {
            if (_registrationSuffix is not null &&
                status.ClientIdSuffix is not null &&
                !string.Equals(_registrationSuffix, status.ClientIdSuffix, StringComparison.Ordinal))
            {
                _broker.Restart();
            }

            if (status.ClientIdSuffix is not null)
                _registrationSuffix = status.ClientIdSuffix;

            _broker.ObserveCredentialState(
                status.Present,
                status.Present ? status.CredentialGeneration : 0);
        }

        return _broker;
    }
}
