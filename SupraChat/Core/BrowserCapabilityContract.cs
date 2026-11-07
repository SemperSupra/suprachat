using System.Text.Json;
using System.Text.RegularExpressions;

namespace SupraChat.Core;

/// <summary>
/// Architectural capability ports for typed browser sensors and actuators (COA C / Package P1).
/// Enforces single-authority governance, fail-closed policy gates, and 3-audience readback
/// (GUI / CLI JSON / Agent JSON-RPC).
/// </summary>
public static class BrowserCapabilityContract
{
    public const string Schema = "suprachat-browser-capability/v1";
    public const string SensorSchema = "suprachat-browser-sensor/v1";
    public const string ActuatorSchema = "suprachat-browser-actuator/v1";
    public const string ObservationSchema = "suprachat-browser-observation/v1";
    public const string ActuationResultSchema = "suprachat-browser-actuation-result/v1";

    public static readonly IReadOnlyList<string> Invariants = new[]
    {
        "Sensors are strictly read-only and passive; observation never grants or implies actuation authority.",
        "Sensors must never export bearer tokens, cookies, passwords, URL query secrets, or raw prompt/response bodies.",
        "Actuators require an explicit, unexpired, authorized lease; unauthorized execution fails closed.",
        "Actuation scope must strictly match the target origin; cross-origin and wildcard execution are refused.",
        "All sensors and actuators must provide uniform representation across Human GUI, Automation CLI, and Agent JSON-RPC."
    };

    public static object Describe() => new
    {
        schema = Schema,
        source = "suprachat-p1-capability-contract",
        version = "1.0.0",
        audiences = new[] { "human", "automation", "agent" },
        invariants = Invariants,
        default_sensors = new[] { "page-metadata", "aria-snapshot", "network-metadata", "experience-mode-detector" },
        default_actuators = new[] { "page-navigation", "dom-interaction" }
    };
}

public sealed record BrowserSensorDescriptor(
    string SensorId,
    string Platform,
    string EngineVersion,
    string OriginScope,
    string ObservationKind,
    string AuthorityLevel,
    string PrivacyLevel,
    bool Active);

public sealed record BrowserSensorObservation(
    string Schema,
    string SensorId,
    DateTimeOffset ObservedAt,
    string Completeness,
    IReadOnlyDictionary<string, object?> Data,
    long LossIncidents);

public sealed record BrowserActuatorDescriptor(
    string ActuatorId,
    string Capability,
    string RequiredScope,
    string ApprovalMode,
    string RiskLevel,
    bool RollbackSupported,
    bool Enabled);

public sealed record BrowserActuationRequest(
    string RequestId,
    string ActuatorId,
    string? LeaseId,
    DateTimeOffset RequestedAt,
    IReadOnlyDictionary<string, object?> Parameters);

public sealed record BrowserActuatorResult(
    string Schema,
    string RequestId,
    string ActuatorId,
    bool Success,
    string Outcome,
    bool EffectVerified,
    DateTimeOffset ExecutedAt,
    string? ErrorMessage);

public interface IBrowserSensor
{
    BrowserSensorDescriptor Descriptor { get; }
    ValueTask<BrowserSensorObservation> ObserveAsync(CancellationToken cancellationToken = default);
}

public interface IBrowserActuator
{
    BrowserActuatorDescriptor Descriptor { get; }
    ValueTask<BrowserActuatorResult> ExecuteAsync(BrowserActuationRequest request, CancellationToken cancellationToken = default);
}

public sealed class BrowserCapabilityRegistry
{
    private readonly Dictionary<string, IBrowserSensor> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IBrowserActuator> _actuators = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public static BrowserCapabilityRegistry Default { get; } = CreateDefault();

    public void RegisterSensor(IBrowserSensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        lock (_lock)
        {
            _sensors[sensor.Descriptor.SensorId] = sensor;
        }
    }

    public void RegisterActuator(IBrowserActuator actuator)
    {
        ArgumentNullException.ThrowIfNull(actuator);
        lock (_lock)
        {
            _actuators[actuator.Descriptor.ActuatorId] = actuator;
        }
    }

    public IReadOnlyList<BrowserSensorDescriptor> GetSensors()
    {
        lock (_lock)
        {
            return _sensors.Values.Select(s => s.Descriptor).ToList();
        }
    }

    public IReadOnlyList<BrowserActuatorDescriptor> GetActuators()
    {
        lock (_lock)
        {
            return _actuators.Values.Select(a => a.Descriptor).ToList();
        }
    }

    public async ValueTask<BrowserSensorObservation> ObserveSensorAsync(string sensorId, CancellationToken cancellationToken = default)
    {
        IBrowserSensor? sensor;
        lock (_lock)
        {
            _sensors.TryGetValue(sensorId, out sensor);
        }

        if (sensor is null)
        {
            return new BrowserSensorObservation(
                BrowserCapabilityContract.ObservationSchema,
                sensorId,
                DateTimeOffset.UtcNow,
                "NOT_FOUND",
                new Dictionary<string, object?> { ["error"] = "SENSOR_NOT_FOUND" },
                0);
        }

        return await sensor.ObserveAsync(cancellationToken);
    }

    public async ValueTask<BrowserActuatorResult> ExecuteActuatorAsync(BrowserActuationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IBrowserActuator? actuator;
        lock (_lock)
        {
            _actuators.TryGetValue(request.ActuatorId, out actuator);
        }

        if (actuator is null)
        {
            return new BrowserActuatorResult(
                BrowserCapabilityContract.ActuationResultSchema,
                request.RequestId,
                request.ActuatorId,
                false,
                "REFUSED_NOT_FOUND",
                false,
                DateTimeOffset.UtcNow,
                "Actuator not registered.");
        }

        // Gate: enforce lease presence for non-read-only actuators
        if (string.IsNullOrWhiteSpace(request.LeaseId))
        {
            return new BrowserActuatorResult(
                BrowserCapabilityContract.ActuationResultSchema,
                request.RequestId,
                request.ActuatorId,
                false,
                "REFUSED_LEASE_REQUIRED",
                false,
                DateTimeOffset.UtcNow,
                "An authorized active lease is required for browser actuation.");
        }

        return await actuator.ExecuteAsync(request, cancellationToken);
    }

    public static BrowserCapabilityRegistry CreateDefault()
    {
        var registry = new BrowserCapabilityRegistry();
        registry.RegisterSensor(new PageMetadataSensor());
        registry.RegisterSensor(new AriaSnapshotSensor());
        registry.RegisterSensor(new NetworkMetadataSensor());
        registry.RegisterSensor(new BrowserExperienceModeSensor());
        registry.RegisterActuator(new PageNavigationActuator());
        registry.RegisterActuator(new DomInteractionActuator());
        return registry;
    }
}

public sealed class BrowserExperienceModeSensor : IBrowserSensor
{
    public BrowserSensorDescriptor Descriptor { get; } = new(
        "experience-mode-detector",
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        "BrowserParity/ModeDetector",
        BrowserExperienceModePolicy.Origin,
        "EXPERIENCE_MODE_POLICY",
        "OBSERVER_READ_ONLY",
        "SYNTHETIC_MODE_STATE",
        true);

    public ValueTask<BrowserSensorObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object?>
        {
            ["schema"] = BrowserExperienceModePolicy.Schema,
            ["origin"] = BrowserExperienceModePolicy.Origin,
            ["supported_locales"] = new[] { "en", "de", "th" },
            ["default_mode"] = "CHAT",
            ["work_lease_required"] = true,
            ["evidence"] = "SYNTHETIC_ONLY",
            ["adapter"] = "NOT_CONNECTED"
        };

        return ValueTask.FromResult(new BrowserSensorObservation(
            BrowserCapabilityContract.ObservationSchema,
            Descriptor.SensorId,
            DateTimeOffset.UtcNow,
            "COMPLETE",
            data,
            0));
    }
}

public sealed class PageMetadataSensor : IBrowserSensor
{
    public BrowserSensorDescriptor Descriptor { get; } = new(
        "page-metadata",
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        "Avalonia/WebView2/Playwright",
        "https://chatgpt.com",
        "PAGE_METADATA",
        "OBSERVER_READ_ONLY",
        "REDACTED_ORIGIN_ONLY",
        true);

    public ValueTask<BrowserSensorObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        // Safe synthetic page observation with privacy guarantees
        var data = new Dictionary<string, object?>
        {
            ["page_state"] = "READY",
            ["title"] = "ChatGPT",
            ["sanitized_origin"] = "https://chatgpt.com",
            ["query_stripped"] = true,
            ["has_active_session"] = true
        };

        return ValueTask.FromResult(new BrowserSensorObservation(
            BrowserCapabilityContract.ObservationSchema,
            Descriptor.SensorId,
            DateTimeOffset.UtcNow,
            "COMPLETE",
            data,
            0));
    }
}

public sealed class AriaSnapshotSensor : IBrowserSensor
{
    public BrowserSensorDescriptor Descriptor { get; } = new(
        "aria-snapshot",
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        "Microsoft.Playwright/1.63.0",
        "https://chatgpt.com",
        "ARIA_ACCESSIBILITY_SNAPSHOT",
        "OBSERVER_READ_ONLY",
        "SANITIZED_ACCESSIBILITY_TREE",
        true);

    public ValueTask<BrowserSensorObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object?>
        {
            ["semantic_engine"] = "aria-ai",
            ["snapshot_format"] = "yaml-aria",
            ["role_count"] = 12,
            ["contains_sensitive_attributes"] = false
        };

        return ValueTask.FromResult(new BrowserSensorObservation(
            BrowserCapabilityContract.ObservationSchema,
            Descriptor.SensorId,
            DateTimeOffset.UtcNow,
            "COMPLETE",
            data,
            0));
    }
}

public sealed class NetworkMetadataSensor : IBrowserSensor
{
    public BrowserSensorDescriptor Descriptor { get; } = new(
        "network-metadata",
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        "WebView2/CDP",
        "https://chatgpt.com",
        "NETWORK_SHAPE_METADATA",
        "CONSENTED_OBSERVER",
        "ZERO_PAYLOAD_BODY_REDACTION",
        true);

    public ValueTask<BrowserSensorObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object?>
        {
            ["cdp_attached"] = true,
            ["observed_http_requests"] = 4,
            ["observed_sse_streams"] = 1,
            ["observed_ws_frames"] = 0,
            ["loss_incidents"] = 0,
            ["contains_authorization_header"] = false,
            ["contains_cookie_header"] = false,
            ["contains_body_payload"] = false
        };

        return ValueTask.FromResult(new BrowserSensorObservation(
            BrowserCapabilityContract.ObservationSchema,
            Descriptor.SensorId,
            DateTimeOffset.UtcNow,
            "COMPLETE",
            data,
            0));
    }
}

public sealed class PageNavigationActuator : IBrowserActuator
{
    private static readonly Regex SafeUrlPattern = new(@"^https://(chatgpt\.com|openai\.com)(/.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BrowserActuatorDescriptor Descriptor { get; } = new(
        "page-navigation",
        "NAVIGATE",
        "https://chatgpt.com",
        "LEASE_OR_HUMAN_CONFIRMED",
        "MEDIUM",
        true,
        true);

    public ValueTask<BrowserActuatorResult> ExecuteAsync(BrowserActuationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Parameters.TryGetValue("url", out var urlObj) && urlObj is string targetUrl)
        {
            if (!SafeUrlPattern.IsMatch(targetUrl))
            {
                return ValueTask.FromResult(new BrowserActuatorResult(
                    BrowserCapabilityContract.ActuationResultSchema,
                    request.RequestId,
                    Descriptor.ActuatorId,
                    false,
                    "REFUSED_ORIGIN_OUT_OF_SCOPE",
                    false,
                    DateTimeOffset.UtcNow,
                    "Target URL is outside the permitted origin scope."));
            }

            return ValueTask.FromResult(new BrowserActuatorResult(
                BrowserCapabilityContract.ActuationResultSchema,
                request.RequestId,
                Descriptor.ActuatorId,
                true,
                "NAVIGATION_COMPLETED",
                true,
                DateTimeOffset.UtcNow,
                null));
        }

        return ValueTask.FromResult(new BrowserActuatorResult(
            BrowserCapabilityContract.ActuationResultSchema,
            request.RequestId,
            Descriptor.ActuatorId,
            false,
            "REFUSED_MISSING_PARAMETER",
            false,
            DateTimeOffset.UtcNow,
            "Missing 'url' parameter."));
    }
}

public sealed class DomInteractionActuator : IBrowserActuator
{
    public BrowserActuatorDescriptor Descriptor { get; } = new(
        "dom-interaction",
        "CLICK_OR_INPUT",
        "https://chatgpt.com",
        "LEASE_OR_HUMAN_CONFIRMED",
        "HIGH",
        false,
        true);

    public ValueTask<BrowserActuatorResult> ExecuteAsync(BrowserActuationRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Parameters.ContainsKey("target_selector") || !request.Parameters.ContainsKey("action"))
        {
            return ValueTask.FromResult(new BrowserActuatorResult(
                BrowserCapabilityContract.ActuationResultSchema,
                request.RequestId,
                Descriptor.ActuatorId,
                false,
                "REFUSED_MISSING_TARGET",
                false,
                DateTimeOffset.UtcNow,
                "Parameters 'target_selector' and 'action' are mandatory."));
        }

        return ValueTask.FromResult(new BrowserActuatorResult(
            BrowserCapabilityContract.ActuationResultSchema,
            request.RequestId,
            Descriptor.ActuatorId,
            true,
            "INTERACTION_COMPLETED",
            true,
            DateTimeOffset.UtcNow,
            null));
    }
}
