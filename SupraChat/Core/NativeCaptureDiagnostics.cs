namespace SupraChat.Core;

internal enum NativeDiagnosticReason
{
    RawEventOversize, MissingRequestId, RequestIdOversize, InvalidRequestUrl, CorrelationCapacity,
    RequestScopeRevoked, MetadataMalformed, CallbackFailure, SinkRefused, SinkUnauthorized, SinkDisposed,
    SinkFailure, AttachFailure, NavigationFailure, UnsubscribeFailure, EnableFailure, DisableFailure, CleanupTimeout
}
public enum NativeCaptureEndReason { None, OperatorStop, SummarySave, LeaseExpired, Navigation, ViewReplaced, OriginUnavailable, ReviewClosed, AttachFailure, RecorderStopped, RecorderUnavailable, ObserverFailure, InactiveUnknown }
public sealed record NativeDiagnosticSnapshot(long ObservationIncidents, long LifecycleIncidents,
    IReadOnlyDictionary<string, long> Reasons)
{
    public long TotalIncidents => ObservationIncidents + LifecycleIncidents;
}

/// <summary>Closed reason codes only. Counts incidents, never a measured number of lost network events.</summary>
internal sealed class NativeCaptureDiagnostics
{
    internal static NativeCaptureEndReason InactiveReason(string state) => state switch
    {
        "EXPIRED" => NativeCaptureEndReason.LeaseExpired,
        "RECORDER_STOPPED" => NativeCaptureEndReason.RecorderStopped,
        "RECORDER_UNAVAILABLE" => NativeCaptureEndReason.RecorderUnavailable,
        _ => NativeCaptureEndReason.InactiveUnknown
    };
    private readonly long[] _counts = new long[Enum.GetValues<NativeDiagnosticReason>().Length];
    internal void Record(NativeDiagnosticReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Interlocked.Increment(ref _counts[(int)reason]);
    }
    internal static bool IsLifecycle(NativeDiagnosticReason reason) => reason is NativeDiagnosticReason.AttachFailure or
        NativeDiagnosticReason.UnsubscribeFailure or NativeDiagnosticReason.EnableFailure or
        NativeDiagnosticReason.DisableFailure or NativeDiagnosticReason.CleanupTimeout;
    internal NativeDiagnosticSnapshot Snapshot()
    {
        var reasons = new Dictionary<string, long>(); long observation = 0, lifecycle = 0;
        foreach (var reason in Enum.GetValues<NativeDiagnosticReason>())
        {
            var count = Interlocked.Read(ref _counts[(int)reason]);
            if (count == 0) continue;
            reasons.Add(reason.ToString(), count);
            if (IsLifecycle(reason)) lifecycle += count; else observation += count;
        }
        return new(observation, lifecycle, reasons);
    }
    internal static NativeDiagnosticSnapshot Merge(NativeDiagnosticSnapshot first, NativeDiagnosticSnapshot second)
    {
        var reasons = new Dictionary<string, long>(first.Reasons);
        foreach (var pair in second.Reasons) reasons[pair.Key] = reasons.GetValueOrDefault(pair.Key) + pair.Value;
        return new(first.ObservationIncidents + second.ObservationIncidents, first.LifecycleIncidents + second.LifecycleIncidents, reasons);
    }
}
