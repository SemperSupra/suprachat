using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

/// <summary>Explicit local-session binding. Recorder failure never changes a provider operation.</summary>
public sealed class OwnedTransportTrace
{
    private readonly TraceNormalizer _normalizer;
    private readonly LocalTraceSession _session;
    private long _recorderFailures;
    public long RecorderFailures => Interlocked.Read(ref _recorderFailures);

    public OwnedTransportTrace(TraceNormalizer normalizer, LocalTraceSession session)
    {
        normalizer.Read(session, limit: 1); // Authorize now, not on a provider callback thread.
        _normalizer = normalizer;
        _session = session;
    }

    public void Record(TraceSurface surface, TraceAdapter adapter, TraceDirection direction,
        TraceEventKind kind, string correlationId, long byteCount = 0, int? status = null, string? requestId = null,
        TraceOutcome outcome = TraceOutcome.Observed)
    {
        try
        {
            if (!_normalizer.IsCapturing(_session)) return;
            _normalizer.TryObserve(_session, new TraceObservation(surface, adapter, direction, kind,
                Origin: surface == TraceSurface.CodexJsonRpc ? null : "https://api.openai.com/",
                RequestId: requestId ?? correlationId, StreamId: correlationId, ByteCount: byteCount, Status: status, Outcome: outcome));
        }
        catch { Interlocked.Increment(ref _recorderFailures); }
    }

    public void RecordJson(TraceSurface surface, TraceAdapter adapter, TraceDirection direction, string correlationId, string json)
    {
        try { if (!_normalizer.IsCapturing(_session)) return; }
        catch { Interlocked.Increment(ref _recorderFailures); return; }
        // Never retain method names, provider event strings, parameters, errors or body fragments.
        var kind = TraceEventKind.Chunk;
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (surface == TraceSurface.CodexJsonRpc)
            {
                if (root.TryGetProperty("id", out var id))
                    requestId = id.ValueKind == JsonValueKind.String ? id.GetString() :
                        id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
                kind = root.TryGetProperty("error", out _) ? TraceEventKind.Error : root.TryGetProperty("method", out _)
                    ? (root.TryGetProperty("id", out _) ? TraceEventKind.Request : TraceEventKind.Chunk)
                    : TraceEventKind.Response;
            }
            else if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                kind = type.GetString() switch
                {
                    "response.create" or "response.created" => TraceEventKind.Request,
                    "response.completed" => TraceEventKind.Completed,
                    "response.failed" or "response.incomplete" or "error" => TraceEventKind.Error,
                    _ => TraceEventKind.Chunk
                };
            }
        }
        catch { kind = TraceEventKind.Error; }
        Record(surface, adapter, direction, kind, correlationId, Encoding.UTF8.GetByteCount(json), requestId: requestId);
    }
}
