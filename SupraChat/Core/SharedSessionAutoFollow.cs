namespace SupraChat.Core;

/// <summary>
/// Strictly read-only, EXPLICITLY started, bounded W6 metadata observer.
/// One awaited GET at a time; never discovers/starts a host, sends a POST,
/// subscribes to ChatGPT product traffic, or operates a terminal.
/// Caller controls stop/cancellation and owns all UI state.
/// </summary>
public static class SharedSessionAutoFollow
{
    public const string Schema = "suprachat-w16-opt-in-metadata-follow/v1";
    public const int MaxObservations = 20;
    public static readonly TimeSpan MinimumCadence = TimeSpan.FromSeconds(3);

    public static async Task<SharedSessionAutoFollowResult> RunAsync(
        string selectedSessionId,
        string expectedHostInstanceId,
        long initialSequence,
        Func<CancellationToken, Task<SharedSessionInventoryReceipt>> observe,
        Func<SharedSessionInventoryReceipt, SharedSessionFeedView, CancellationToken, Task> onObservation,
        CancellationToken stop,
        Func<CancellationToken, Task>? betweenObservations = null,
        int maxObservations = MaxObservations)
    {
        if (!Guid.TryParseExact(selectedSessionId, "N", out _) ||
            !Guid.TryParseExact(expectedHostInstanceId, "N", out _) ||
            initialSequence < 0 ||
            maxObservations is < 1 or > MaxObservations)
            throw new ArgumentException("INVALID_AUTO_FOLLOW_ADMISSION");
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(onObservation);
        // Synthetic qualification may inject a no-delay step; production
        // always uses a fixed >=3-second cadence, no runtime timer globals.
        var pause = betweenObservations ?? (ct => Task.Delay(MinimumCadence, ct));
        var sequence = initialSequence;
        var count = 0;
        try
        {
            for (var i = 0; i < maxObservations; i++)
            {
                stop.ThrowIfCancellationRequested();
                // Never overlap network requests and never accumulate a queue.
                var snapshot = await observe(stop).ConfigureAwait(false);
                stop.ThrowIfCancellationRequested();
                var projected = SharedSessionFeedProjection.Project(snapshot,
                    selectedSessionId, sequence, expectedHostInstanceId);
                if (!snapshot.Connected)
                    return new(Schema, "STOPPED_HOST_READ_REFUSED", count, sequence,
                        ContinuousHistoryProven:false, RealChatGptHistoryObserved:false);
                if (projected.EventGapUnverified ||
                    projected.SelectedSessionId != selectedSessionId ||
                    projected.Status is "INVALID_CURSOR" or "INVALID_HOST_EPOCH" or
                        "SEQUENCE_REWIND_RESELECT_REQUIRED" or
                        "SELECTED_SESSION_MISSING_RESELECT_REQUIRED" or
                        "HOST_INSTANCE_CHANGED_RESELECT_REQUIRED")
                    return new(Schema, "STOPPED_CONTINUITY_UNVERIFIED_" + projected.Status,
                        count, sequence,
                        ContinuousHistoryProven:false, RealChatGptHistoryObserved:false);
                // A GUI dispatcher may be stalled while Stop/window-close cancels.
                // Don't hold the network observer hostage to a queued presentation.
                // Callbacks MUST still check the token before touching UI state:
                // cancellation releases this wait, not arbitrary callback side effects.
                await onObservation(snapshot, projected, stop)
                    .WaitAsync(stop).ConfigureAwait(false);
                stop.ThrowIfCancellationRequested();
                sequence = snapshot.Sequence;
                count++;
                // Do not delay or perform a 21st network operation.
                if (i + 1 < maxObservations)
                    await pause(stop).ConfigureAwait(false);
            }
            return new(Schema, "COMPLETED_BOUNDED_OBSERVATIONS", count, sequence,
                ContinuousHistoryProven:false, RealChatGptHistoryObserved:false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return new(Schema, "STOPPED_BY_USER_OR_WINDOW", count, sequence,
                ContinuousHistoryProven:false, RealChatGptHistoryObserved:false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or
                                   InvalidOperationException or OperationCanceledException)
        {
            return new(Schema, "STOPPED_OBSERVATION_ERROR", count, sequence,
                ContinuousHistoryProven:false, RealChatGptHistoryObserved:false);
        }
    }
}

public sealed record SharedSessionAutoFollowResult(
    string Schema, string Status, int DeliveredObservations,
    long LastSequence, bool ContinuousHistoryProven, bool RealChatGptHistoryObserved);
