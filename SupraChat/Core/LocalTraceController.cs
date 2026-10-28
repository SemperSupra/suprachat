using System.Text.Json;
using System.Runtime.Versioning;

namespace SupraChat.Core;

internal interface ILocalTraceObserver
{
    bool IsStopped { get; }
    void StopObserving();
}

/// <summary>One local operator/process. Never a cross-process actor/session authority.</summary>
public sealed class LocalTraceController : IDisposable
{
    private readonly TraceNormalizer _normalizer = new();
    private readonly LocalTraceSession _session;
    private readonly List<ILocalTraceObserver> _nativeAdapters = new();
    private readonly object _nativeGate = new();
    private bool _disposed;
    private int _pendingNativeAttachments;
    public OwnedTransportTrace Binding { get; }
    public LocalOwnedStreamControls Controls { get; }

    public LocalTraceController()
    {
        _session = _normalizer.CreateSession();
        Binding = new OwnedTransportTrace(_normalizer, _session);
        Controls = new LocalOwnedStreamControls(_session.SessionId);
    }

    [SupportedOSPlatform("windows")]
    public async Task<WindowsWebViewTraceAdapter> AttachSyntheticNativeAsync(IntPtr handle, Uri origin)
    {
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _nativeAdapters.RemoveAll(a => a.IsStopped);
            if (_nativeAdapters.Count + _pendingNativeAttachments >= 16)
                throw new UnauthorizedAccessException("Native attachment capacity reached.");
            _pendingNativeAttachments++;
        }
        try
        {
            var adapter = await WindowsWebViewTraceAdapter.AttachSyntheticAsync(handle, _normalizer, _session, origin);
            lock (_nativeGate)
            {
                if (!_disposed) { _nativeAdapters.Add(adapter); return adapter; }
            }
            {
                await adapter.DisposeAsync();
                throw new ObjectDisposedException(nameof(LocalTraceController));
            }
        }
        finally { lock (_nativeGate) _pendingNativeAttachments--; }
    }

    public JsonElement Execute(string operation, JsonElement? parameters = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long after = 0;
        var limit = 100;
        Guid streamId = Guid.Empty, planId = Guid.Empty, actionKey = Guid.Empty;
        long revision = 0;
        var action = OwnedControlAction.Cancel;
        var confirmed = false;
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
                    case "stream_id" when operation == "control/preview": streamId = property.Value.GetGuid(); break;
                    case "plan_id" when operation == "control/apply": planId = property.Value.GetGuid(); break;
                    case "action_key" when operation is "control/preview" or "control/apply": actionKey = property.Value.GetGuid(); break;
                    case "expected_revision" when operation is "control/preview" or "control/apply": revision = property.Value.GetInt64(); break;
                    case "confirmed" when operation == "control/apply": confirmed = property.Value.GetBoolean(); break;
                    case "action" when operation == "control/preview":
                        action = property.Value.GetString() switch
                        {
                            "cancel" => OwnedControlAction.Cancel, "synthetic_delay" => OwnedControlAction.SyntheticDelay,
                            "synthetic_drop" => OwnedControlAction.SyntheticDrop, "synthetic_fault" => OwnedControlAction.SyntheticFault,
                            "synthetic_replay" => OwnedControlAction.SyntheticReplay, "modify_first_party" => OwnedControlAction.ModifyFirstParty,
                            "capture_payload" => OwnedControlAction.CapturePayload, _ => throw new UnauthorizedAccessException("Control action refused.")
                        };
                        break;
                    default:
                        throw new UnauthorizedAccessException("Trace parameter or authority claim refused.");
                }
            }
        }

        if (operation == "control/preview") return JsonSerializer.SerializeToElement(
            Controls.Preview(_session.SessionId, streamId, action, revision, actionKey), TraceNormalizer.JsonOptions);
        if (operation == "control/apply") return JsonSerializer.SerializeToElement(
            Controls.Apply(_session.SessionId, planId, revision, actionKey, confirmed), TraceNormalizer.JsonOptions);
        if (operation == "streams") return JsonSerializer.SerializeToElement(new
        {
            session_id = _session.SessionId, authority = "SINGLE_OPERATOR_PROCESS_LOCAL",
            streams = Controls.List().Select(s => new
            {
                s.StreamId, s.Surface, s.Revision, s.CancellationRequested,
                trace_stream_id = _normalizer.StreamAlias(_session, s.StreamId.ToString("N"))
            }).ToArray()
        }, TraceNormalizer.JsonOptions);

        switch (operation)
        {
            case "start": _normalizer.SetCapture(_session, TraceCaptureMode.Metadata); break;
            case "stop": _normalizer.SetCapture(_session, TraceCaptureMode.Off); break;
            case "status": case "read": case "export": break;
            default: throw new UnauthorizedAccessException("Trace operation refused.");
        }

        var page = _normalizer.Read(_session, after, limit);
        bool nativeAttached;
        lock (_nativeGate) nativeAttached = OperatingSystem.IsWindows() && _nativeAdapters.Any(a => !a.IsStopped);
        return JsonSerializer.SerializeToElement(new
        {
            schema = "suprachat-trace-control/v1", operation,
            scope = "SINGLE_OPERATOR_PROCESS_LOCAL_OWNED_TRANSPORTS",
            session_id = _session.SessionId, mode = page.Mode,
            recorder_failures = Binding.RecorderFailures,
            capture_gap = page.CaptureGap || Binding.RecorderFailures > 0,
            payload_capture = false, browser_capture = nativeAttached && page.Mode == TraceCaptureMode.Metadata,
            browser_observer_attached = nativeAttached,
            browser_scope = nativeAttached ? "SYNTHETIC_LOOPBACK_ONLY" : "NONE", cross_actor_authority = false,
            page
        }, TraceNormalizer.JsonOptions);
    }

    public void Dispose()
    {
        ILocalTraceObserver[] adapters;
        lock (_nativeGate)
        {
            if (_disposed) return;
            _disposed = true;
            adapters = _nativeAdapters.ToArray();
            _nativeAdapters.Clear();
        }
        if (OperatingSystem.IsWindows())
            foreach (var adapter in adapters) adapter.StopObserving();
        Controls.Dispose();
        _normalizer.Dispose();
    }
}
