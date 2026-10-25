namespace SupraChat.Core;

/// <summary>
/// Explicitly invoked, bounded read-only observation of one preselected W6
/// host session. The caller owns start, cancellation and presentation.
/// It cannot select another session, start a host, submit a ChatGPT turn,
/// send any ledger POST, or attest the loopback host's real identity.
/// </summary>
public static class SharedSessionAutoFollow
{
    public const string Schema = "suprachat-w16-bounded-auto-follow/v1";
    public const int MaxObservations = 64;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    public static async Task<SharedSessionAutoFollowOutcome> RunAsync(
        Func<CancellationToken, Task<SharedSessionInventoryReceipt>> observe,
        Func<SharedSessionFeedView, CancellationToken, Task> publish,
        string selectedSessionId, long previousSequence,
        string previousHostInstanceId, CancellationToken cancellationToken,
        TimeSpan? interval = null, int maximumObservations = MaxObservations)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(publish);
        if (!Guid.TryParseExact(selectedSessionId, "N", out _) ||
            !Guid.TryParseExact(previousHostInstanceId, "N", out _) ||
            !string.Equals(Guid.Parse(previousHostInstanceId).ToString("N"),
                previousHostInstanceId, StringComparison.Ordinal) ||
            previousSequence < 0)
            throw new ArgumentException("AUTO_FOLLOW_REQUIRES_EXPLICIT_VALID_SELECTION_CURSOR_AND_EPOCH");
        if (maximumObservations is < 1 or > MaxObservations)
            throw new ArgumentOutOfRangeException(nameof(maximumObservations));
        var period = interval ?? DefaultInterval;
        if (period < TimeSpan.FromMilliseconds(20) || period > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(interval));

        var lastSequence = previousSequence;
        var observations = 0;
        try
        {
            while (observations < maximumObservations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Exactly one outstanding read. The reader must enforce a
                // complete-response deadline and use the supplied token.
                var receipt = await observe(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var view = SharedSessionFeedProjection.Project(receipt,
                    selectedSessionId, lastSequence, previousHostInstanceId);
                observations++;
                // Backpressure: do not issue a new read until the consumer
                // has finished accepting the previous projection.
                await publish(view, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (view.Status is not ("SELECTED_NEW_EVENTS" or "SELECTED_NO_NEW_EVENTS"))
                    return new(Schema, "STOPPED_UNVERIFIED_" + view.Status,
                        observations, lastSequence, ReadOnly: true,
                        RemoteChatAuthority: false, TerminalAuthority: false);
                lastSequence = view.Sequence;
                if (observations < maximumObservations)
                    await Task.Delay(period, cancellationToken).ConfigureAwait(false);
            }
            return new(Schema, "STOPPED_BOUNDED_OBSERVATION_LIMIT",
                observations, lastSequence, ReadOnly: true,
                RemoteChatAuthority: false, TerminalAuthority: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(Schema, "STOPPED_BY_USER_OR_LIFECYCLE",
                observations, lastSequence, ReadOnly: true,
                RemoteChatAuthority: false, TerminalAuthority: false);
        }
        catch (Exception) // No token, payload, endpoint or exception text in receipts.
        {
            return new(Schema, "STOPPED_OBSERVER_OR_PRESENTATION_FAILURE",
                observations, lastSequence, ReadOnly: true,
                RemoteChatAuthority: false, TerminalAuthority: false);
        }
    }
}

public sealed record SharedSessionAutoFollowOutcome(
    string Schema, string Status, int Observations, long LastSequence,
    bool ReadOnly, bool RemoteChatAuthority, bool TerminalAuthority);
