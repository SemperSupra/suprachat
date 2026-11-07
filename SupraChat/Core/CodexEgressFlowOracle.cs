using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace SupraChat.Core;

public sealed record CodexCliInventory(
    [property: JsonPropertyName("resolved_executable_name")] string ResolvedExecutableName,
    [property: JsonPropertyName("bundled_present")] bool BundledPresent,
    [property: JsonPropertyName("version_contract")] string VersionContract,
    [property: JsonPropertyName("provider_binding")] string ProviderBinding,
    [property: JsonPropertyName("listen_transport")] string ListenTransport,
    [property: JsonPropertyName("token_in_arguments")] bool TokenInArguments);

public sealed record OtelPostureDescriptor(
    [property: JsonPropertyName("default_export_enabled")] bool DefaultExportEnabled,
    [property: JsonPropertyName("trace_id_format")] string TraceIdFormat,
    [property: JsonPropertyName("active_trace_id")] string ActiveTraceId,
    [property: JsonPropertyName("contains_prompts")] bool ContainsPrompts,
    [property: JsonPropertyName("contains_tokens")] bool ContainsTokens,
    [property: JsonPropertyName("unconsented_export_policy")] string UnconsentedExportPolicy);

public sealed record EgressLaneStatus(
    [property: JsonPropertyName("lane_id")] string LaneId,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("coverage_state")] string CoverageState,
    [property: JsonPropertyName("missing_path_reason")] string? MissingPathReason,
    [property: JsonPropertyName("production_reroute_permitted")] bool ProductionReroutePermitted,
    [property: JsonPropertyName("flow_correlation_alias")] string FlowCorrelationAlias);

public sealed record PlaywrightSidecarStatus(
    [property: JsonPropertyName("packaging_mode")] string PackagingMode,
    [property: JsonPropertyName("playwright_assembly_version")] string PlaywrightAssemblyVersion,
    [property: JsonPropertyName("bundled_browser_directory_present")] bool BundledBrowserDirectoryPresent,
    [property: JsonPropertyName("native_first_decoupling_supported")] bool NativeFirstDecouplingSupported,
    [property: JsonPropertyName("sidecar_fallback_policy")] string SidecarFallbackPolicy);

public sealed record CodexEgressEvaluationReceipt(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("grade")] string Grade,
    [property: JsonPropertyName("suite_id")] string SuiteId,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("evaluated_at_utc")] string EvaluatedAtUtc,
    [property: JsonPropertyName("production_routing_posture")] string ProductionRoutingPosture,
    [property: JsonPropertyName("codex_cli")] CodexCliInventory CodexCli,
    [property: JsonPropertyName("otel_posture")] OtelPostureDescriptor OtelPosture,
    [property: JsonPropertyName("egress_lanes")] IReadOnlyList<EgressLaneStatus> EgressLanes,
    [property: JsonPropertyName("playwright_runtime")] PlaywrightSidecarStatus PlaywrightRuntime,
    [property: JsonPropertyName("all_invariants_satisfied")] bool AllInvariantsSatisfied);

public sealed record EgressMutationDecision(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("effect")] string Effect);

/// <summary>
/// Architectural implementation of Campaign Package P7 (Suite T14):
/// Codex CLI/version/OTEL + host egress + isolated Hyper-V/WSL/gateway flow correlation
/// and optional Playwright runtime decoupling status.
/// Enforces strict NO_PRODUCTION_REROUTE and default-off redacted telemetry invariants.
/// </summary>
public static class CodexEgressFlowOracle
{
    public const string Schema = "suprachat-codex-egress-correlation/v1";
    public const string Grade = "QUALIFIED_SYNTHETIC";
    public const string SuiteId = "T14";
    public const string NoProductionReroutePosture = "NO_PRODUCTION_REROUTE";

    private static readonly byte[] CorrelationKey = SHA256.HashData(
        Encoding.UTF8.GetBytes("suprachat-p7-egress-flow-correlation-v1"));

    public static CodexEgressEvaluationReceipt Evaluate(string? sessionSeed = null)
    {
        var seed = string.IsNullOrWhiteSpace(sessionSeed) ? "synthetic-egress-session-001" : sessionSeed;
        var resolvedPath = CodexAppServer.ResolveExecutable();
        var executableName = Path.GetFileName(resolvedPath);
        var bundledPresent = Path.IsPathRooted(resolvedPath) && File.Exists(resolvedPath);
        var args = CodexAppServer.Arguments;
        var tokenInArgs = args.Any(a => a.Contains("Bearer ", StringComparison.OrdinalIgnoreCase));

        var codexInventory = new CodexCliInventory(
            ResolvedExecutableName: executableName,
            BundledPresent: bundledPresent,
            VersionContract: "codex-app-server/stdio-v1",
            ProviderBinding: "openai_chatgpt_plan",
            ListenTransport: "stdio://",
            TokenInArguments: tokenInArgs);

        var observability = DogfoodObservability.Describe();
        var otelPosture = new OtelPostureDescriptor(
            DefaultExportEnabled: false,
            TraceIdFormat: "W3C-32HEX",
            ActiveTraceId: observability.TraceId,
            ContainsPrompts: observability.ContainsPrompts,
            ContainsTokens: observability.ContainsTokenMaterial,
            UnconsentedExportPolicy: "REFUSED_BY_DEFAULT");

        var lanes = new EgressLaneStatus[]
        {
            new(
                LaneId: "windows-host-loopback",
                Environment: "Windows Host",
                CoverageState: "COVERED_SYNTHETIC",
                MissingPathReason: null,
                ProductionReroutePermitted: false,
                FlowCorrelationAlias: ComputeFlowAlias(seed, "windows-host-loopback")),
            new(
                LaneId: "wsl2-namespace-bridge",
                Environment: "WSL2 Namespace",
                CoverageState: "SYNTHETIC_PROBE_ONLY",
                MissingPathReason: "LIVE_WSL_NAMESPACE_REQUIRES_LOCAL_HOST_HIL",
                ProductionReroutePermitted: false,
                FlowCorrelationAlias: ComputeFlowAlias(seed, "wsl2-namespace-bridge")),
            new(
                LaneId: "hyperv-isolated-gateway",
                Environment: "Hyper-V VPN Gateway (#29)",
                CoverageState: "SYNTHETIC_PROBE_ONLY",
                MissingPathReason: "ISOLATED_VM_SWITCH_REQUIRES_LOCAL_HYPERV_HIL",
                ProductionReroutePermitted: false,
                FlowCorrelationAlias: ComputeFlowAlias(seed, "hyperv-isolated-gateway"))
        };

        var browserStatus = BrowserSession.Status();
        var playwrightStatus = new PlaywrightSidecarStatus(
            PackagingMode: browserStatus.BrowserDirectoryPresent ? "BUNDLED_RUNTIME" : "DECOUPLED_OPTIONAL_SIDECAR",
            PlaywrightAssemblyVersion: typeof(IPlaywright).Assembly.GetName().Version?.ToString() ?? "unknown",
            BundledBrowserDirectoryPresent: browserStatus.BrowserDirectoryPresent,
            NativeFirstDecouplingSupported: true,
            SidecarFallbackPolicy: "NATIVE_WEBVIEW2_PRIMARY_PLAYWRIGHT_OPTIONAL");

        var allInvariants =
            !codexInventory.TokenInArguments &&
            !otelPosture.DefaultExportEnabled &&
            !otelPosture.ContainsPrompts &&
            !otelPosture.ContainsTokens &&
            lanes.All(l => !l.ProductionReroutePermitted) &&
            playwrightStatus.NativeFirstDecouplingSupported;

        return new CodexEgressEvaluationReceipt(
            Schema: Schema,
            Grade: Grade,
            SuiteId: SuiteId,
            Platform: PlatformName(),
            EvaluatedAtUtc: DogfoodObservability.UtcTimestamp(),
            ProductionRoutingPosture: NoProductionReroutePosture,
            CodexCli: codexInventory,
            OtelPosture: otelPosture,
            EgressLanes: lanes,
            PlaywrightRuntime: playwrightStatus,
            AllInvariantsSatisfied: allInvariants);
    }

    public static EgressMutationDecision EvaluateRouteChangeAttempt(
        string targetLane,
        bool mutatesProductionRoute,
        bool unconsentedOtelExport)
    {
        if (mutatesProductionRoute)
        {
            return new EgressMutationDecision(
                Allowed: false,
                Status: "REFUSED_PRODUCTION_REROUTE",
                Reason: $"Production routing or gateway table mutation on lane '{targetLane}' is strictly forbidden.",
                Effect: "NONE");
        }

        if (unconsentedOtelExport)
        {
            return new EgressMutationDecision(
                Allowed: false,
                Status: "REFUSED_UNCONSENTED_OTEL_EXPORT",
                Reason: "External OpenTelemetry export without explicit operator consent is forbidden.",
                Effect: "NONE");
        }

        return new EgressMutationDecision(
            Allowed: true,
            Status: "PERMITTED_SYNTHETIC_OBSERVATION",
            Reason: "Passive synthetic flow correlation with zero route mutation.",
            Effect: "READ_ONLY_CORRELATION");
    }

    public static string ComputeFlowAlias(string sessionSeed, string laneId)
    {
        var input = Encoding.UTF8.GetBytes($"{sessionSeed}:{laneId}");
        var mac = HMACSHA256.HashData(CorrelationKey, input);
        return Convert.ToHexString(mac)[..20].ToLowerInvariant();
    }

    private static string PlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }
}
