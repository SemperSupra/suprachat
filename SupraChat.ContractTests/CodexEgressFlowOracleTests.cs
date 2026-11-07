using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class CodexEgressFlowOracleTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        // 1. Evaluate synthetic baseline receipt (T14)
        var receipt = CodexEgressFlowOracle.Evaluate("session-egress-alpha");
        Require(receipt.Schema == CodexEgressFlowOracle.Schema, "Codex egress schema mismatch");
        Require(receipt.Grade == CodexEgressFlowOracle.Grade, "Codex egress grade mismatch");
        Require(receipt.SuiteId == "T14", "Codex egress suite ID must be T14");
        Require(receipt.ProductionRoutingPosture == "NO_PRODUCTION_REROUTE", "Production routing posture must be NO_PRODUCTION_REROUTE");
        Require(receipt.AllInvariantsSatisfied, "All P7 egress and OTEL invariants must be satisfied");

        // 2. Codex CLI inventory checks
        Require(receipt.CodexCli.ProviderBinding == "openai_chatgpt_plan", "Codex CLI provider binding mismatch");
        Require(receipt.CodexCli.ListenTransport == "stdio://", "Codex CLI listen transport must be stdio://");
        Require(!receipt.CodexCli.TokenInArguments, "Codex CLI arguments must never contain bearer tokens");

        // 3. OTEL posture checks
        Require(!receipt.OtelPosture.DefaultExportEnabled, "OTEL external export must be disabled by default");
        Require(!receipt.OtelPosture.ContainsPrompts, "OTEL posture must exclude prompts");
        Require(!receipt.OtelPosture.ContainsTokens, "OTEL posture must exclude token material");
        Require(receipt.OtelPosture.UnconsentedExportPolicy == "REFUSED_BY_DEFAULT", "Unconsented OTEL policy must be REFUSED_BY_DEFAULT");
        Require(receipt.OtelPosture.ActiveTraceId.Length == 32, "Active W3C trace ID must be 32 hex chars");

        // 4. Egress lanes & flow correlation checks (Windows Host, WSL2, Hyper-V Gateway)
        Require(receipt.EgressLanes.Count == 3, "Expected 3 egress lanes (Windows Host, WSL2, Hyper-V)");
        var hostLane = receipt.EgressLanes.Single(l => l.LaneId == "windows-host-loopback");
        var wslLane = receipt.EgressLanes.Single(l => l.LaneId == "wsl2-namespace-bridge");
        var hypervLane = receipt.EgressLanes.Single(l => l.LaneId == "hyperv-isolated-gateway");

        Require(hostLane.CoverageState == "COVERED_SYNTHETIC" && hostLane.MissingPathReason == null, "Host loopback lane must be covered synthetically");
        Require(wslLane.CoverageState == "SYNTHETIC_PROBE_ONLY" && !string.IsNullOrEmpty(wslLane.MissingPathReason), "WSL2 lane must explicitly report missing live path in synthetic mode");
        Require(hypervLane.CoverageState == "SYNTHETIC_PROBE_ONLY" && !string.IsNullOrEmpty(hypervLane.MissingPathReason), "Hyper-V lane must explicitly report missing live path in synthetic mode");

        foreach (var lane in receipt.EgressLanes)
        {
            Require(!lane.ProductionReroutePermitted, $"Lane {lane.LaneId} must forbid production rerouting");
            Require(lane.FlowCorrelationAlias.Length == 20, $"Lane {lane.LaneId} correlation alias must be 20 hex chars");
        }

        // Verify session-scoped alias isolation
        var receiptBeta = CodexEgressFlowOracle.Evaluate("session-egress-beta");
        var betaHostAlias = receiptBeta.EgressLanes.Single(l => l.LaneId == "windows-host-loopback").FlowCorrelationAlias;
        Require(hostLane.FlowCorrelationAlias != betaHostAlias, "Different session seeds must produce distinct HMAC flow correlation aliases");

        // 5. Playwright runtime decoupling status
        Require(receipt.PlaywrightRuntime.NativeFirstDecouplingSupported, "Native-first Playwright decoupling must be supported");
        Require(receipt.PlaywrightRuntime.SidecarFallbackPolicy == "NATIVE_WEBVIEW2_PRIMARY_PLAYWRIGHT_OPTIONAL", "Sidecar fallback policy mismatch");

        // 6. Fail-closed route mutation and unconsented OTEL export checks
        var prodRerouteAttempt = CodexEgressFlowOracle.EvaluateRouteChangeAttempt(
            targetLane: "hyperv-isolated-gateway",
            mutatesProductionRoute: true,
            unconsentedOtelExport: false);
        Require(!prodRerouteAttempt.Allowed, "Production route mutation must be refused");
        Require(prodRerouteAttempt.Status == "REFUSED_PRODUCTION_REROUTE", "Status must be REFUSED_PRODUCTION_REROUTE");

        var unconsentedOtelAttempt = CodexEgressFlowOracle.EvaluateRouteChangeAttempt(
            targetLane: "windows-host-loopback",
            mutatesProductionRoute: false,
            unconsentedOtelExport: true);
        Require(!unconsentedOtelAttempt.Allowed, "Unconsented OTEL export must be refused");
        Require(unconsentedOtelAttempt.Status == "REFUSED_UNCONSENTED_OTEL_EXPORT", "Status must be REFUSED_UNCONSENTED_OTEL_EXPORT");

        var passiveObservation = CodexEgressFlowOracle.EvaluateRouteChangeAttempt(
            targetLane: "windows-host-loopback",
            mutatesProductionRoute: false,
            unconsentedOtelExport: false);
        Require(passiveObservation.Allowed, "Passive synthetic observation must be permitted");
        Require(passiveObservation.Status == "PERMITTED_SYNTHETIC_OBSERVATION", "Status must be PERMITTED_SYNTHETIC_OBSERVATION");

        // 7. Binding-aware auth scope verification (codex-app-server vs direct REST API)
        var codexScopedPayload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"scp":["openid","profile","email","offline_access","model.request"]}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var codexScopedJwt = $"eyJhbGciOiJSUzI1NiJ9.{codexScopedPayload}.sig";
        Require(!SubscriptionCredentialStore.HasDirectRestScopes(codexScopedJwt),
            "Codex-only subscription JWT without direct REST scopes must not report HasDirectRestScopes=true");

        var directRestPayload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"scp":["openid","chatgpt.tokens.use.direct","api.model.read"]}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var directRestJwt = $"eyJhbGciOiJSUzI1NiJ9.{directRestPayload}.sig";
        Require(SubscriptionCredentialStore.HasDirectRestScopes(directRestJwt),
            "Direct REST scoped JWT must report HasDirectRestScopes=true");

        await Task.CompletedTask;
    }
}
