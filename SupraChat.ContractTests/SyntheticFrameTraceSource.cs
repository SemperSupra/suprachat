using System.Runtime.CompilerServices;

namespace SupraChat.Core;

/// <summary>
/// Synthetic fixture output as actual trace observations, never network traffic.
/// Deliberately carries only fixed route/category and integer frame metadata.
/// </summary>
public sealed class SyntheticFrameTraceSource(SyntheticFrameReceipt receipt) : ITraceSource
{
    public async IAsyncEnumerable<TraceObservation> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        const string origin = "http://127.0.0.1/fixture";
        const string request = "synthetic-only-operation";
        const string stream = "synthetic-only-stream";
        static TraceObservation Event(TraceEventKind kind, long sequence, long byteCount = 0) =>
            new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Ingress,
                kind, origin, request, stream, sequence, byteCount);

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return Event(TraceEventKind.Request, 0);

        if (receipt.SimulatedFailure)
        {
            yield return Event(TraceEventKind.Error, 1);
            yield return Event(TraceEventKind.CaptureGap, 2);
            yield break;
        }

        yield return Event(TraceEventKind.Response, 1);
        for (var i = 0; i < receipt.OutputFrames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            // The extra synthetic replay frame has the same sequence as the previous source frame.
            var sourceSequence = receipt.Treatment == SyntheticFrameTreatment.RepeatLast &&
                i == receipt.InputFrames ? receipt.InputFrames + 1 : i + 2;
            yield return Event(TraceEventKind.Chunk, sourceSequence, 1);
        }

        if (receipt.Treatment == SyntheticFrameTreatment.DropLast ||
            receipt.Treatment == SyntheticFrameTreatment.RepeatLast)
        {
            yield return Event(TraceEventKind.CaptureGap, receipt.InputFrames + 2);
        }
        // The drop fixture does not synthesize a successful completion after missing a frame.
        if (receipt.Treatment != SyntheticFrameTreatment.DropLast)
            yield return Event(TraceEventKind.Completed, receipt.InputFrames + 3);
    }
}
