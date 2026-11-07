using System.Text.Json;
using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class BrowserCapabilityContractTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        // 1. Contract metadata and 3-audience parity
        var contractObj = BrowserCapabilityContract.Describe();
        var json = JsonSerializer.Serialize(contractObj);
        Require(json.Contains("suprachat-browser-capability/v1", StringComparison.Ordinal), "Schema missing in contract description");
        Require(json.Contains("human", StringComparison.Ordinal) && json.Contains("automation", StringComparison.Ordinal) && json.Contains("agent", StringComparison.Ordinal), "3-audience parity missing");

        // 2. Default registry configuration
        var registry = BrowserCapabilityRegistry.Default;
        var sensors = registry.GetSensors();
        var actuators = registry.GetActuators();

        Require(sensors.Count >= 3, "Expected at least 3 default sensors");
        Require(sensors.Any(s => s.SensorId == "page-metadata"), "page-metadata sensor missing");
        Require(sensors.Any(s => s.SensorId == "aria-snapshot"), "aria-snapshot sensor missing");
        Require(sensors.Any(s => s.SensorId == "network-metadata"), "network-metadata sensor missing");

        Require(actuators.Count >= 2, "Expected at least 2 default actuators");
        Require(actuators.Any(a => a.ActuatorId == "page-navigation"), "page-navigation actuator missing");
        Require(actuators.Any(a => a.ActuatorId == "dom-interaction"), "dom-interaction actuator missing");

        // 3. Sensor observation & redaction invariants
        var pageObs = await registry.ObserveSensorAsync("page-metadata");
        Require(pageObs.Schema == BrowserCapabilityContract.ObservationSchema, "Observation schema mismatch");
        Require(pageObs.Completeness == "COMPLETE", "Observation completeness mismatch");
        Require(pageObs.Data.ContainsKey("sanitized_origin"), "Sanitized origin missing from page-metadata");

        var netObs = await registry.ObserveSensorAsync("network-metadata");
        Require((bool)(netObs.Data["contains_authorization_header"] ?? true) == false, "Network observation must not contain authorization header");
        Require((bool)(netObs.Data["contains_cookie_header"] ?? true) == false, "Network observation must not contain cookie header");
        Require((bool)(netObs.Data["contains_body_payload"] ?? true) == false, "Network observation must not contain raw body payloads");

        var unknownObs = await registry.ObserveSensorAsync("non-existent-sensor");
        Require(unknownObs.Completeness == "NOT_FOUND", "Unknown sensor must report NOT_FOUND completeness");

        // 4. Actuator lease gating (fail-closed)
        var unleasedReq = new BrowserActuationRequest(
            RequestId: "req-unleased-001",
            ActuatorId: "page-navigation",
            LeaseId: null,
            RequestedAt: DateTimeOffset.UtcNow,
            Parameters: new Dictionary<string, object?> { ["url"] = "https://chatgpt.com" });

        var unleasedResult = await registry.ExecuteActuatorAsync(unleasedReq);
        Require(!unleasedResult.Success, "Unleased actuation must fail");
        Require(unleasedResult.Outcome == "REFUSED_LEASE_REQUIRED", "Unleased actuation must fail closed with REFUSED_LEASE_REQUIRED");

        // 5. Actuator origin scope gating
        var outOfScopeReq = new BrowserActuationRequest(
            RequestId: "req-scope-001",
            ActuatorId: "page-navigation",
            LeaseId: "lease-valid-001",
            RequestedAt: DateTimeOffset.UtcNow,
            Parameters: new Dictionary<string, object?> { ["url"] = "https://malicious-site.example.com/exploit" });

        var outOfScopeResult = await registry.ExecuteActuatorAsync(outOfScopeReq);
        Require(!outOfScopeResult.Success, "Out-of-scope actuation must fail");
        Require(outOfScopeResult.Outcome == "REFUSED_ORIGIN_OUT_OF_SCOPE", "Out-of-scope actuation must report REFUSED_ORIGIN_OUT_OF_SCOPE");

        // 6. Actuator parameter validation
        var missingParamReq = new BrowserActuationRequest(
            RequestId: "req-param-001",
            ActuatorId: "page-navigation",
            LeaseId: "lease-valid-001",
            RequestedAt: DateTimeOffset.UtcNow,
            Parameters: new Dictionary<string, object?>());

        var missingParamResult = await registry.ExecuteActuatorAsync(missingParamReq);
        Require(!missingParamResult.Success, "Missing parameter actuation must fail");
        Require(missingParamResult.Outcome == "REFUSED_MISSING_PARAMETER", "Missing parameter must report REFUSED_MISSING_PARAMETER");

        // 7. Successful authorized actuation within origin scope
        var validNavReq = new BrowserActuationRequest(
            RequestId: "req-nav-001",
            ActuatorId: "page-navigation",
            LeaseId: "lease-valid-001",
            RequestedAt: DateTimeOffset.UtcNow,
            Parameters: new Dictionary<string, object?> { ["url"] = "https://chatgpt.com/chat" });

        var validNavResult = await registry.ExecuteActuatorAsync(validNavReq);
        Require(validNavResult.Success, "Valid in-scope leased navigation must succeed");
        Require(validNavResult.Outcome == "NAVIGATION_COMPLETED", "Navigation outcome mismatch");
        Require(validNavResult.EffectVerified, "Navigation effect must be verified");

        // 8. Dom interaction actuator
        var domReq = new BrowserActuationRequest(
            RequestId: "req-dom-001",
            ActuatorId: "dom-interaction",
            LeaseId: "lease-valid-001",
            RequestedAt: DateTimeOffset.UtcNow,
            Parameters: new Dictionary<string, object?>
            {
                ["target_selector"] = "button[data-testid='send-button']",
                ["action"] = "CLICK"
            });

        var domResult = await registry.ExecuteActuatorAsync(domReq);
        Require(domResult.Success, "Valid dom interaction must succeed");
        Require(domResult.Outcome == "INTERACTION_COMPLETED", "Dom interaction outcome mismatch");
    }
}
