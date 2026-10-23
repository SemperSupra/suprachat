using System.Text.Json;

namespace SupraChat.Core;

/// <summary>One local operator/process. Never a cross-process actor/session authority.</summary>
public sealed class LocalTraceController : IDisposable
{
    private readonly TraceNormalizer _normalizer = new();
    private readonly LocalTraceSession _session;
    public OwnedTransportTrace Binding { get; }

    public LocalTraceController()
    {
        _session = _normalizer.CreateSession();
        Binding = new OwnedTransportTrace(_normalizer, _session);
    }

    public JsonElement Execute(string operation, JsonElement? parameters = null)
    {
        long after = 0;
        var limit = 100;
        if (parameters is { } p)
        {
            if (p.ValueKind != JsonValueKind.Object) throw new ArgumentException("Trace parameters must be an object.");
            foreach (var property in p.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "session_id":
                        if (property.Value.ValueKind != JsonValueKind.String || !property.Value.TryGetGuid(out var id) || id != _session.SessionId)
                            throw new UnauthorizedAccessException("Trace session refused.");
                        break;
                    case "after_sequence" when operation is "read" or "export":
                        if (!property.Value.TryGetInt64(out after)) throw new ArgumentException("Invalid trace cursor.");
                        break;
                    case "limit" when operation is "read" or "export":
                        if (!property.Value.TryGetInt32(out limit)) throw new ArgumentException("Invalid trace page limit.");
                        break;
                    default:
                        throw new UnauthorizedAccessException("Trace parameter or authority claim refused.");
                }
            }
        }

        switch (operation)
        {
            case "start": _normalizer.SetCapture(_session, TraceCaptureMode.Metadata); break;
            case "stop": _normalizer.SetCapture(_session, TraceCaptureMode.Off); break;
            case "status": case "read": case "export": break;
            default: throw new UnauthorizedAccessException("Trace operation refused.");
        }

        var page = _normalizer.Read(_session, after, limit);
        return JsonSerializer.SerializeToElement(new
        {
            schema = "suprachat-trace-control/v1", operation,
            scope = "SINGLE_OPERATOR_PROCESS_LOCAL_OWNED_TRANSPORTS",
            session_id = _session.SessionId, mode = page.Mode,
            recorder_failures = Binding.RecorderFailures,
            capture_gap = page.CaptureGap || Binding.RecorderFailures > 0,
            payload_capture = false, browser_capture = false, cross_actor_authority = false,
            page
        }, TraceNormalizer.JsonOptions);
    }

    public void Dispose() => _normalizer.Dispose();
}
