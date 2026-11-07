using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using SupraChat.Core;

namespace SupraChat.Automation;

internal static class Program
{
    private const string Schema = "suprachat-machine/v1";
    private static readonly JsonSerializerOptions CliJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private static readonly JsonSerializerOptions LineJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private static readonly SemaphoreSlim StdoutGate = new(1, 1);
    private static readonly LocalTraceController Trace = new();
    private static readonly ResponsesClient TracedResponses = new(Trace.Binding, Trace.Controls);
    private static readonly SupraChatCore Core = new(responses: TracedResponses);

    private static CodexAppServerClient? _agentCodex;
    private static CodexAppServerClient.CodexEventSubscription? _agentCodexEvents;
    private static CancellationTokenSource? _agentCodexCts;
    private static Task? _agentCodexPump;

    private static BrowserSession? _agentBrowser;

    private static ResponsesWebSocketClient? _agentResponsesSocket;
    private static CancellationTokenSource? _agentResponsesCts;
    private static Task? _agentResponsesPump;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                WriteJson(Help());
                return 0;
            }

            return args[0] switch
            {
                "capabilities" => WriteSuccess(Capabilities()),
                "accessibility" => WriteSuccess(Accessibility()),
                "accessibility-preferences" => WriteSuccess(await AccessibilityPreferencesAsync()),
                "accessibility-set" => WriteSuccess(await SetAccessibilityPreferencesAsync(args[1..])),
                "parity" => WriteSuccess(ReadCatalog("audience-parity-20261001.json")),
                "catalog" => WriteSuccess(ReadCombinedCatalog()),
                "codex-catalog" => WriteSuccess(ReadCatalog("codex-capability-catalog-20261001.json")),
                "siwc-catalog" => WriteSuccess(ReadCatalog("siwc-capability-surface-20261001.json")),
                "doctor" => WriteSuccess(await DoctorAsync()),
                "diagnostics" => WriteSuccess(DogfoodObservability.Describe()),
                "trace-status" => WriteSuccess(Trace.Execute("status")),
                "trace-session" => await RunTraceSessionAsync(),
                "diagnostics-export" => WriteSuccess(await DiagnosticsExportAsync(args[1..])),
                "auth-status" => WriteSuccess(await AuthStatusAsync()),
                "auth-policy" => WriteSuccess(await AuthPolicyAsync()),
                "auth-policy-set" => WriteSuccess(await AuthPolicySetAsync(args[1..])),
                "auth-login" => WriteSuccess(await AuthLoginAsync(args[1..])),
                "auth-set-api-key" => WriteSuccess(await AuthSetApiKeyAsync(args[1..])),
                "auth-clear-api-key" => WriteSuccess(await AuthClearApiKeyAsync()),
                "auth-clear" => WriteSuccess(await AuthClearAsync(args[1..])),
                "auth-bundle-inspect" => WriteSuccess(await AuthBundleInspectAsync(args[1..])),
                "auth-export" => WriteSuccess(await AuthExportAsync(args[1..])),
                "auth-import" => WriteSuccess(await AuthImportAsync(args[1..])),
                "models" => WriteSuccess(await ModelsAsync(args[1..])),
                "voices" => WriteSuccess(await CodexRpcAsync(new[] { "--method", "thread/realtime/listVoices", "--params", "{}" })),
                "remote-status" => WriteSuccess(await CodexRpcAsync(new[] { "--method", "remoteControl/status/read" })),
                "remote-enable" => WriteSuccess(await RemoteEnableAsync(args[1..])),
                "remote-disable" => WriteSuccess(await RemoteDisableAsync(args[1..])),
                "remote-pair" => WriteSuccess(await RemotePairAsync(args[1..])),
                "remote-pair-status" => WriteSuccess(await RemotePairStatusAsync(args[1..])),
                "remote-clients" => WriteSuccess(await RemoteClientsAsync(args[1..])),
                "remote-revoke" => WriteSuccess(await RemoteRevokeAsync(args[1..])),
                "plugins" => WriteSuccess(await CodexRpcAsync(new[] { "--method", "plugin/list", "--params", "{}" })),
                "permission-profiles" => WriteSuccess(await CodexRpcAsync(new[] { "--method", "permissionProfile/list", "--params", "{}" })),
                "apps" => WriteSuccess(await CodexRpcAsync(new[] { "--method", "app/list", "--params", "{}" })),
                "sandbox-readiness" => WriteSuccess(await CodexRpcAsync(new[] { "--method", "windowsSandbox/readiness" })),
                "screen-status" => WriteSuccess(DesktopScreenCapture.Describe()),
                "screen-capture" => WriteSuccess(await ScreenCaptureAsync(args[1..])),
                "browser-status" => WriteSuccess(BrowserStatus()),
                "browser-sensors" => WriteSuccess(BrowserCapabilityRegistry.Default.GetSensors()),
                "browser-actuators" => WriteSuccess(BrowserCapabilityRegistry.Default.GetActuators()),
                "browser-sensor-observe" => WriteSuccess(await BrowserSensorObserveAsync(args[1..])),
                "browser-actuator-execute" => WriteSuccess(await BrowserActuatorExecuteAsync(args[1..])),
                "browser-mode-preflight" => WriteSuccess(BrowserModePreflight(args[1..])),
                "browser-trace-snapshot" => WriteSuccess(BrowserTraceSnapshot(args[1..])),
                "browser-differential-evaluate" => WriteSuccess(BrowserDifferentialEvaluate(args[1..])),
                "userscript-status" => WriteSuccess(UserscriptStatus(args[1..])),
                "userscript-verify" => WriteSuccess(UserscriptVerify(args[1..])),
                "codex-egress-status" => WriteSuccess(CodexEgressStatus(args[1..])),
                "browser-snapshot" => WriteSuccess(await BrowserSnapshotAsync(args[1..])),
                "browser-click" => WriteSuccess(await BrowserClickConfirmedAsync(args[1..])),
                "browser-fill" => WriteSuccess(await BrowserFillConfirmedAsync(args[1..])),
                "respond" => WriteSuccess(await RespondAsync(args[1..])),
                "responses-raw" => WriteSuccess(await RawResponsesAsync(args[1..])),
                "codex-rpc" => WriteSuccess(await CodexRpcAsync(args[1..])),
                "session-ledger-contract" => WriteSuccess(SharedSessionLedger.DescribeContract()),
                "session-ledger-host" => await RunSessionLedgerHostAsync(args[1..]),
                "session-ledger-list" => WriteSuccess(await SessionLedgerClient.RequestAsync(
                    ParseLedgerPort(Option(args[1..], "--port")), "list")),
                "session-ledger-open" => WriteSuccess(await SessionLedgerClient.RequestAsync(
                    ParseLedgerPort(Option(args[1..], "--port")), "open", await ReadLedgerStdinAsync())),
                "session-ledger-change" => WriteSuccess(await SessionLedgerClient.RequestAsync(
                    ParseLedgerPort(Option(args[1..], "--port")), "change", await ReadLedgerStdinAsync())),
                "session-ledger-revoke-own" => WriteSuccess(await SessionLedgerClient.RequestAsync(
                    ParseLedgerPort(Option(args[1..], "--port")), "revoke-own", await ReadLedgerStdinAsync())),
                "session-draft-review" => WriteSuccess(await SessionDraftReviewAsync(args[1..])),
                "session-draft-stage" => WriteSuccess(await SessionDraftStageAsync(args[1..])),
                "session-draft-read" => WriteSuccess(await SessionDraftReadAsync(args[1..])),
                "session-draft-discard" => WriteSuccess(await SessionDraftDiscardAsync(args[1..])),
                "session-export" => WriteSuccess(await SessionExportAsync(args[1..])),
                "session-status" => WriteSuccess(await SessionStatusAsync(args[1..])),
                "session-close" => WriteSuccess(await SessionCloseAsync(args[1..])),
                "session-restore" => WriteSuccess(await SessionRestoreAsync(args[1..])),
                "stdio" => await RunStdioAsync(),
                _ => WriteFailure(2, "USAGE", $"Unknown command: {args[0]}", Help())
            };
        }
        catch (MachineException ex)
        {
            return WriteFailure(ex.ExitCode, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return WriteFailure(4, "FAILED", SafeMessage(ex));
        }
    }

    private static object Help() => new
    {
        schema = Schema,
        product = "SupraChat",
        audience = new[] { "human", "automation", "agent" },
        commands = new object[]
        {
            new { name = "capabilities", description = "Read machine/product capability metadata." },
            new { name = "accessibility", description = "Read the cross-platform accessibility/UI/UX/DX contract." },
            new { name = "accessibility-preferences", description = "Read effective local accessibility preferences." },
            new { name = "accessibility-set", description = "Set local accessibility preferences.", syntax = "accessibility-set [--scale <0.8-2.0>] [--reduced-motion <true|false>]" },
            new { name = "parity", description = "Read machine-readable human/accessibility/automation/agent parity by capability." },
            new { name = "catalog", description = "Read the packaged SIWC + Codex capability catalogs." },
            new { name = "codex-catalog", description = "Read packaged Codex stable-runtime + upstream-frontier surfaces." },
            new { name = "siwc-catalog", description = "Read packaged SIWC / ChatGPT-plan capability metadata." },
            new { name = "doctor", description = "Inspect local runtime/auth readiness without network calls." },
            new { name = "diagnostics", description = "Read privacy-safe dogfood observability metadata, trace IDs, and local log paths." },
            new { name = "diagnostics-export", description = "Export a privacy-safe diagnostics bundle.", syntax = "diagnostics-export --output <path.zip>" },
            new { name = "auth-status", description = "Read redacted local ChatGPT-plan authorization state." },
            new { name = "auth-policy", description = "Read effective authentication priority order and provider readiness." },
            new { name = "auth-policy-set", description = "Configure authentication priority order (e.g. subscription,api_key,siwc).", syntax = "auth-policy-set --order <csv> [--prefer-codex <true|false>] [--custom-api-key-env <name>]" },
            new { name = "auth-login", description = "Login via subscription (device-auth, token, or existing Codex session), SIWC, or API key.", syntax = "auth-login [--subscription|--device-auth|--api-key <key>|--token <token>|--manual]" },
            new { name = "auth-set-api-key", description = "Save an OpenAI API key in local protected storage.", syntax = "auth-set-api-key --key <api-key>" },
            new { name = "auth-clear-api-key", description = "Clear saved OpenAI API key from local protected storage." },
            new { name = "auth-clear", description = "Clear all saved local session tokens." },
            new { name = "auth-bundle-inspect", description = "Inspect redacted metadata from an encrypted portable SIWC credential bundle.", syntax = "auth-bundle-inspect --input <path>" },
            new { name = "auth-export", description = "Export the active renewable SIWC credential to an encrypted portable bundle.", syntax = "auth-export --confirm --output <path> --passphrase-env <ENV_VAR>" },
            new { name = "auth-import", description = "Import an encrypted portable SIWC credential bundle while preserving this host's identity.", syntax = "auth-import --confirm --input <path> --passphrase-env <ENV_VAR>" },
            new { name = "models", description = "List models visible to the saved ChatGPT-plan authorization." },
            new { name = "voices", description = "List realtime voices exposed by the bundled Codex runtime." },
            new { name = "remote-status", description = "Read Codex Remote connection/identity status without enabling or pairing." },
            new { name = "remote-enable", description = "Explicitly enable Codex Remote.", syntax = "remote-enable --confirm [--ephemeral]" },
            new { name = "remote-disable", description = "Explicitly disable Codex Remote.", syntax = "remote-disable --confirm [--ephemeral]" },
            new { name = "remote-pair", description = "Explicitly start remote pairing.", syntax = "remote-pair --confirm [--manual-code]" },
            new { name = "remote-pair-status", description = "Read pairing claim status.", syntax = "remote-pair-status [--pairing-code <code>] [--manual-code <code>]" },
            new { name = "remote-clients", description = "List paired remote clients.", syntax = "remote-clients --environment <id>" },
            new { name = "remote-revoke", description = "Explicitly revoke a paired remote client.", syntax = "remote-revoke --confirm --environment <id> --client <id>" },
            new { name = "plugins", description = "List available Codex plugins without installing or mutating them." },
            new { name = "permission-profiles", description = "List effective Codex permission profiles without changing them." },
            new { name = "apps", description = "List available Codex apps/connectors without installing or changing them." },
            new { name = "sandbox-readiness", description = "Read Windows Codex sandbox readiness without starting setup." },
            new { name = "screen-status", description = "Read the current platform screen-capture adapter and permission boundary." },
            new { name = "screen-capture", description = "Explicitly capture the current desktop to a PNG file.", syntax = "screen-capture --output <path.png>" },
            new { name = "browser-status", description = "Read bundled clean-room browser runtime status." },
            new { name = "browser-sensors", description = "List registered typed browser sensors and capability contracts." },
            new { name = "browser-actuators", description = "List registered typed browser actuators and authority requirements." },
            new { name = "browser-sensor-observe", description = "Query observation data from a registered browser sensor.", syntax = "browser-sensor-observe [--id <sensor-id>]" },
            new { name = "browser-actuator-execute", description = "Execute actuation through an authorized lease gate.", syntax = "browser-actuator-execute --id <actuator-id> --lease <lease-id> [--url <url>] [--selector <sel>] [--action <act>]" },
            new { name = "browser-mode-preflight", description = "Evaluate fail-closed Chat vs Work experience mode policy preflight.", syntax = "browser-mode-preflight [--intent <CHAT|WORK>] [--session <id>] [--epoch <num>] [--lease-confirmed] [--audience <human|automation|agent>]" },
            new { name = "browser-trace-snapshot", description = "Read synthetic-only consented browser trace shape snapshot with HMAC aliases and capture gaps.", syntax = "browser-trace-snapshot [--origin <url>] [--limit <num>]" },
            new { name = "browser-differential-evaluate", description = "Evaluate multi-vantage two-session differential, security invariant, and accessibility acceptance.", syntax = "browser-differential-evaluate [--session-a <id>] [--session-b <id>] [--intent-a <CHAT|WORK>] [--intent-b <CHAT|WORK>] [--lease-b]" },
            new { name = "userscript-status", description = "Read managed userscript registry and promo UI status.", syntax = "userscript-status [--id <id>]" },
            new { name = "userscript-verify", description = "Cryptographically verify and register candidate userscript against its SHA-256 hash.", syntax = "userscript-verify --id <id> [--script <text>]" },
            new { name = "codex-egress-status", description = "Evaluate Codex CLI/OTEL posture, host/WSL/Hyper-V egress flow correlation, and Playwright sidecar decoupling.", syntax = "codex-egress-status [--seed <session-seed>]" },
            new { name = "browser-snapshot", description = "Navigate and return an ARIA semantic snapshot.", syntax = "browser-snapshot --url <https-url>" },
            new { name = "browser-click", description = "Explicitly click one element by ARIA role and accessible name.", syntax = "browser-click --confirm --url <https-url> --role <role> --name <accessible-name>" },
            new { name = "browser-fill", description = "Explicitly fill one field by accessible label.", syntax = "browser-fill --confirm --url <https-url> --label <label> --value <text>" },
            new { name = "respond", description = "Run a typed streamed Responses request.", syntax = "respond --model <id> --input <text> [--file <path>]... [--web-search]" },
            new { name = "responses-raw", description = "Run an arbitrary SIWC Responses body.", syntax = "responses-raw --model <id> [--body <json>]; stdin is used when --body is omitted" },
            new { name = "codex-rpc", description = "Invoke one Codex app-server RPC.", syntax = "codex-rpc --method <name> [--params <json>]" },
            new { name = "trace-status", description = "Read this process's metadata trace status; capture starts OFF." },
            new { name = "trace-session", description = "Run persistent JSON-line automation commands: trace status/start/stop/read/export and owned respond/responses-raw/codex-rpc actions." },
            new { name = "session-ledger-contract", description = "Read synthetic W6 session core limits; no external authority qualification." },
            new { name = "session-ledger-host", description = "Explicitly run a disposable loopback-only W6 ledger; requires synthetic-mode environment gate." },
            new { name = "session-ledger-list", description = "Inspect the running synthetic W6 ledger; requires explicit port and token." },
            new { name = "session-ledger-open", description = "Create synthetic ledger metadata from stdin; not an authenticated principal." },
            new { name = "session-ledger-change", description = "CAS synthetic ledger metadata from stdin; not a provider action." },
            new { name = "session-ledger-revoke-own", description = "W13 synthetic: explicitly revoke the currently signed actor grant, durably; cannot revoke another actor.", syntax = "session-ledger-revoke-own --port <host-port> < request.json" },
            new { name = "session-draft-review", description = "Prepare and review an isolated local draft transfer (chat-to-terminal or terminal-to-chat).", syntax = "session-draft-review --session <id> --direction <chat-to-terminal|terminal-to-chat> [--text <text>] [--stdin] [--port <port>]" },
            new { name = "session-draft-stage", description = "Stage a reviewed draft transfer into this session's local text buffer after verifying confirm text.", syntax = "session-draft-stage --session <id> --review-id <id> [--confirm-text <text>] [--stdin] [--port <port>]" },
            new { name = "session-draft-read", description = "Read local staged draft buffers for the specified session.", syntax = "session-draft-read --session <id> [--port <port>]" },
            new { name = "session-draft-discard", description = "Discard local draft buffers and pending reviews.", syntax = "session-draft-discard [--session <id>] [--port <port>]" },
            new { name = "session-export", description = "Export metadata-only session representation in JSON or Markdown.", syntax = "session-export --format <json|markdown> [--output <path>] [--session <id>] [--port <port>]" },
            new { name = "session-status", description = "Update parallel session indicator state (idle, running, waiting, error).", syntax = "session-status --session <id> --status <idle|running|waiting|error> [--port <port>]" },
            new { name = "session-close", description = "Archive and close an active session, isolating draft buffers.", syntax = "session-close --session <id> [--port <port>]" },
            new { name = "session-restore", description = "Restore an archived session back to the active list.", syntax = "session-restore --session <id> [--port <port>]" },
            new { name = "stdio", description = "Serve line-delimited JSON-RPC 2.0 for agent clients with sessionful Codex events." }
        },
        auth_boundary = "Interactive authorization is completed by a human through the GUI. Machine shells reuse the same protected local credential store."
    };

    private static object Capabilities() => new
    {
        schema = Schema,
        product = "SupraChat",
        platform = PlatformName(),
        audiences = new
        {
            human = new { ui = "desktop-gui", supported = true },
            automation = new { ui = "console-json", supported = true },
            agent = new { ui = "json-rpc-stdio", supported = true }
        },
        bindings = new[]
        {
            "chatgpt-product-web",
            "siwc-responses",
            "responses-websocket",
            "codex-app-server"
        },
        machine_methods = new[]
        {
            "session/ledger/read",
            "session/ledger/list",
            "session/ledger/open",
            "session/ledger/change",
            "session/ledger/revoke-own",
            "session/draft/review",
            "session/draft/stage",
            "session/draft/read",
            "session/draft/discard",
            "session/export",
            "session/status/set",
            "session/close",
            "session/restore",
            "capabilities/read",
            "accessibility/read",
            "accessibility/preferences/read",
            "accessibility/preferences/write",
            "parity/read",
            "catalog/read",
            "codex/catalog",
            "siwc/catalog",
            "doctor/read",
            "diagnostics/read",
            "diagnostics/export",
            "trace/status", "trace/start", "trace/stop", "trace/read", "trace/export", "trace/streams", "control/preview", "control/apply",
            "auth/status",
            "auth/bundle/inspect",
            "auth/export",
            "auth/import",
            "broker/status",
            "lease/acquire",
            "lease/status",
            "lease/release",
            "models/list",
            "realtime/voices",
            "remote/status",
            "remote/enable",
            "remote/disable",
            "remote/pairing/start",
            "remote/pairing/status",
            "remote/clients/list",
            "remote/clients/revoke",
            "plugins/list",
            "permissions/profiles",
            "apps/list",
            "sandbox/readiness",
            "screen/status",
            "screen/capture",
            "browser/status",
            "browser/sensors/list",
            "browser/actuators/list",
            "browser/sensor/observe",
            "browser/actuator/execute",
            "browser/mode/preflight",
            "browser/trace/snapshot",
            "browser/differential/evaluate",
            "userscript/list",
            "userscript/status",
            "userscript/verify",
            "codex/egress/status",
            "browser/snapshot",
            "browser/start",
            "browser/navigate",
            "browser/read",
            "browser/click",
            "browser/fill",
            "browser/stop",
            "responses/create",
            "responses/raw",
            "responses/ws/connect",
            "responses/ws/send",
            "responses/ws/disconnect",
            "codex/start",
            "codex/request",
            "codex/respond",
            "codex/reject",
            "codex/stop"
        },
        machine_notifications = new[] { "responses/ws/event", "codex/event" },
        invariants = new
        {
            tokens_in_output = false,
            prompts_in_receipts = false,
            human_authorization_boundary = true,
            local_credentials_shared_with_gui = true,
            codex_runtime_resolution = "bundled-first",
            json_rpc_framing = "one-json-object-per-line",
            accessibility_contract = AccessibilityContract.Schema
        }
    };

    private static AccessibilityDescriptor Accessibility() => AccessibilityContract.Describe();

    private static object BrowserStatus() => BrowserSession.Status();

    private static async Task<object> BrowserSnapshotAsync(string[] args)
    {
        var url = Option(args, "--url");
        if (string.IsNullOrWhiteSpace(url))
            throw new MachineException(2, "INVALID_PARAMS", "browser-snapshot requires --url <http-or-https-url>.");

        try
        {
            await using var session = await BrowserSession.StartAsync(headless: true);
            return await session.NavigateAndSnapshotAsync(url);
        }
        catch (PlaywrightException ex)
        {
            throw new MachineException(4, "BROWSER_RUNTIME_UNAVAILABLE", SafeMessage(ex));
        }
    }

    private static async Task<object> BrowserClickOnceAsync(string[] args)
    {
        var url = Option(args, "--url");
        var role = Option(args, "--role");
        var name = Option(args, "--name");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(name))
            throw new MachineException(2, "INVALID_PARAMS", "browser-click requires --url, --role and --name.");

        try
        {
            await using var session = await BrowserSession.StartAsync(headless: true);
            await session.NavigateAndSnapshotAsync(url);
            return await session.ClickByRoleAsync(role, name);
        }
        catch (PlaywrightException ex)
        {
            throw new MachineException(4, "BROWSER_ACTION_FAILED", SafeMessage(ex));
        }
    }

    private static async Task<object> BrowserFillOnceAsync(string[] args)
    {
        var url = Option(args, "--url");
        var label = Option(args, "--label");
        var value = Option(args, "--value");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(label) || value is null)
            throw new MachineException(2, "INVALID_PARAMS", "browser-fill requires --url, --label and --value.");

        try
        {
            await using var session = await BrowserSession.StartAsync(headless: true);
            await session.NavigateAndSnapshotAsync(url);
            return await session.FillByLabelAsync(label, value);
        }
        catch (PlaywrightException ex)
        {
            throw new MachineException(4, "BROWSER_ACTION_FAILED", SafeMessage(ex));
        }
    }

    private static async Task<object> BrowserClickConfirmedAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Browser click");
        return await BrowserClickOnceAsync(args);
    }

    private static async Task<object> BrowserFillConfirmedAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Browser fill");
        return await BrowserFillOnceAsync(args);
    }

    private static async Task<object> BrowserSensorObserveAsync(string[] args)
    {
        var id = Option(args, "--id") ?? "page-metadata";
        return await BrowserCapabilityRegistry.Default.ObserveSensorAsync(id);
    }

    private static async Task<object> BrowserActuatorExecuteAsync(string[] args)
    {
        var id = Option(args, "--id");
        if (string.IsNullOrWhiteSpace(id))
            throw new MachineException(2, "INVALID_PARAMS", "browser-actuator-execute requires --id <actuator-id>.");

        var lease = Option(args, "--lease");
        var url = Option(args, "--url");
        var selector = Option(args, "--selector");
        var action = Option(args, "--action");

        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(url)) parameters["url"] = url;
        if (!string.IsNullOrWhiteSpace(selector)) parameters["target_selector"] = selector;
        if (!string.IsNullOrWhiteSpace(action)) parameters["action"] = action;

        var request = new BrowserActuationRequest(
            RequestId: Guid.NewGuid().ToString("N"),
            ActuatorId: id,
            LeaseId: lease,
            RequestedAt: DateTimeOffset.UtcNow,
            Parameters: parameters);

        return await BrowserCapabilityRegistry.Default.ExecuteActuatorAsync(request);
    }

    private static async Task<object> RpcBrowserSensorObserveAsync(JsonElement? parameters)
    {
        var id = "page-metadata";
        if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object && parameters.Value.TryGetProperty("id", out var idProp))
        {
            id = idProp.GetString() ?? "page-metadata";
        }
        return await BrowserCapabilityRegistry.Default.ObserveSensorAsync(id);
    }

    private static async Task<object> RpcBrowserActuatorExecuteAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var id = RequiredProperty(p, "id");
        var leaseId = p.TryGetProperty("lease_id", out var l) ? l.GetString() : null;
        var reqId = p.TryGetProperty("request_id", out var r) ? r.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");

        var dict = new Dictionary<string, object?>();
        if (p.TryGetProperty("parameters", out var paramObj) && paramObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in paramObj.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString()
                };
            }
        }

        var request = new BrowserActuationRequest(
            RequestId: reqId,
            ActuatorId: id,
            LeaseId: leaseId,
            RequestedAt: DateTimeOffset.UtcNow,
            Parameters: dict);

        return await BrowserCapabilityRegistry.Default.ExecuteActuatorAsync(request);
    }

    private static object BrowserModePreflight(string[] args)
    {
        var intent = Option(args, "--intent") ?? "CHAT";
        var session = Option(args, "--session") ?? "session-local-001";
        var epochStr = Option(args, "--epoch");
        var epoch = long.TryParse(epochStr, out var ep) ? ep : 1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var candidate = new BrowserExperienceModePolicy.CandidatePayload(
            Source: "SYNTHETIC_TYPED_ADAPTER",
            Origin: BrowserExperienceModePolicy.Origin,
            Session: session,
            Epoch: epoch,
            Timestamp: now - 100,
            Locale: "en",
            Controls: new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", true, intent == "CHAT"),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Work", true, intent == "WORK")
            });

        BrowserExperienceModePolicy.HostWorkLease? lease = null;
        var leaseConfirmed = HasFlag(args, "--lease-confirmed");
        if (leaseConfirmed)
        {
            lease = new BrowserExperienceModePolicy.HostWorkLease(
                Type: "EXPLICIT_HOST_WORK_LEASE",
                Session: session,
                Epoch: epoch,
                Confirmed: true,
                Expires: now + 60000);
        }

        var decision = BrowserExperienceModePolicy.Preflight(new BrowserExperienceModePolicy.PreflightRequest(
            Intent: intent,
            Candidate: candidate,
            Session: session,
            Epoch: epoch,
            Now: now,
            Lease: lease));

        var audience = Option(args, "--audience") ?? "automation";
        return BrowserExperienceModePolicy.ForAudience(decision, audience);
    }

    private static object RpcBrowserModePreflight(JsonElement? parameters)
    {
        var intent = "CHAT";
        var session = "session-local-001";
        long epoch = 1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        BrowserExperienceModePolicy.CandidatePayload? candidate = null;
        BrowserExperienceModePolicy.HostWorkLease? lease = null;
        var audience = "agent";

        if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
        {
            var p = parameters.Value;
            if (p.TryGetProperty("intent", out var i) && i.ValueKind == JsonValueKind.String)
                intent = i.GetString() ?? "CHAT";
            if (p.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String)
                session = s.GetString() ?? session;
            if (p.TryGetProperty("epoch", out var ep) && ep.TryGetInt64(out var epVal))
                epoch = epVal;
            if (p.TryGetProperty("audience", out var a) && a.ValueKind == JsonValueKind.String)
                audience = a.GetString() ?? audience;
            if (p.TryGetProperty("lease", out var l) && l.ValueKind == JsonValueKind.Object)
            {
                var lType = l.TryGetProperty("type", out var lt) ? lt.GetString() : null;
                var lSession = l.TryGetProperty("session", out var ls) ? ls.GetString() : null;
                long? lEpoch = l.TryGetProperty("epoch", out var le) && le.TryGetInt64(out var lev) ? lev : null;
                bool? lConf = l.TryGetProperty("confirmed", out var lc) && lc.ValueKind is JsonValueKind.True or JsonValueKind.False ? lc.GetBoolean() : null;
                long? lExp = l.TryGetProperty("expires", out var lx) && lx.TryGetInt64(out var lxv) ? lxv : null;
                lease = new BrowserExperienceModePolicy.HostWorkLease(lType, lSession, lEpoch, lConf, lExp);
            }
        }

        candidate ??= new BrowserExperienceModePolicy.CandidatePayload(
            Source: "SYNTHETIC_TYPED_ADAPTER",
            Origin: BrowserExperienceModePolicy.Origin,
            Session: session,
            Epoch: epoch,
            Timestamp: now - 100,
            Locale: "en",
            Controls: new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", true, intent == "CHAT"),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Work", true, intent == "WORK")
            });

        var decision = BrowserExperienceModePolicy.Preflight(new BrowserExperienceModePolicy.PreflightRequest(
            Intent: intent,
            Candidate: candidate,
            Session: session,
            Epoch: epoch,
            Now: now,
            Lease: lease));

        return BrowserExperienceModePolicy.ForAudience(decision, audience);
    }

    private static object BrowserTraceSnapshot(string[] args)
    {
        var origin = Option(args, "--origin") ?? "https://chatgpt.com";
        var limitStr = Option(args, "--limit");
        var limit = int.TryParse(limitStr, out var lim) ? lim : 32;

        var oracle = new SyntheticTraceShapeOracle(origin, limit);
        var handle = oracle.Session();
        oracle.Enable(handle, true);
        return oracle.Snapshot(handle);
    }

    private static object RpcBrowserTraceSnapshot(JsonElement? parameters)
    {
        var origin = "https://chatgpt.com";
        var limit = 32;

        if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
        {
            var p = parameters.Value;
            if (p.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.String)
                origin = o.GetString() ?? origin;
            if (p.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lVal))
                limit = lVal;
        }

        var oracle = new SyntheticTraceShapeOracle(origin, limit);
        var handle = oracle.Session();
        oracle.Enable(handle, true);
        return oracle.Snapshot(handle);
    }

    private static object BrowserDifferentialEvaluate(string[] args)
    {
        var sessionA = Option(args, "--session-a") ?? "session-synthetic-alpha";
        var sessionB = Option(args, "--session-b") ?? "session-synthetic-beta";
        var intentA = Option(args, "--intent-a") ?? "CHAT";
        var intentB = Option(args, "--intent-b") ?? "WORK";
        var leaseB = HasFlag(args, "--lease-b");

        return MultiVantageDifferentialOracle.EvaluateSyntheticBaseline(
            sessionAId: sessionA,
            sessionBId: sessionB,
            intentA: intentA,
            intentB: intentB,
            leaseConfirmedB: leaseB);
    }

    private static object RpcBrowserDifferentialEvaluate(JsonElement? parameters)
    {
        var sessionA = "session-synthetic-alpha";
        var sessionB = "session-synthetic-beta";
        var intentA = "CHAT";
        var intentB = "WORK";
        var leaseB = false;

        if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
        {
            var p = parameters.Value;
            if (p.TryGetProperty("session_a", out var sa) && sa.ValueKind == JsonValueKind.String)
                sessionA = sa.GetString() ?? sessionA;
            if (p.TryGetProperty("session_b", out var sb) && sb.ValueKind == JsonValueKind.String)
                sessionB = sb.GetString() ?? sessionB;
            if (p.TryGetProperty("intent_a", out var ia) && ia.ValueKind == JsonValueKind.String)
                intentA = ia.GetString() ?? intentA;
            if (p.TryGetProperty("intent_b", out var ib) && ib.ValueKind == JsonValueKind.String)
                intentB = ib.GetString() ?? intentB;
            if (p.TryGetProperty("lease_b", out var lb) && lb.ValueKind is JsonValueKind.True or JsonValueKind.False)
                leaseB = lb.GetBoolean();
        }

        return MultiVantageDifferentialOracle.EvaluateSyntheticBaseline(
            sessionAId: sessionA,
            sessionBId: sessionB,
            intentA: intentA,
            intentB: intentB,
            leaseConfirmedB: leaseB);
    }

    private static object UserscriptStatus(string[] args)
    {
        var id = Option(args, "--id");
        if (!string.IsNullOrEmpty(id))
        {
            var manifest = ManagedUserscriptManager.Default.GetManifest(id);
            return manifest ?? (object)new { error = "NOT_FOUND", id };
        }
        return ManagedUserscriptManager.Default.Describe();
    }

    private static object UserscriptVerify(string[] args)
    {
        var id = Option(args, "--id") ?? "chatgpt-lock-chat-mode";
        var script = Option(args, "--script") ?? ManagedUserscriptManager.CanonicalChatLockScript;
        var manifest = ManagedUserscriptManager.Default.GetManifest(id) ?? new UserscriptManifest(
            Id: id,
            Name: id,
            Version: "1.0.0",
            Origin: ManagedUserscriptManager.CanonicalOrigin,
            RunAt: "document-start",
            ContentHash: ManagedUserscriptManager.ComputeSha256(script),
            Grants: new[] { "none" });

        return ManagedUserscriptManager.Default.VerifyAndRegister(manifest, script);
    }

    private static object RpcUserscriptStatus(JsonElement? parameters)
    {
        if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object &&
            parameters.Value.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String)
        {
            var id = idElem.GetString();
            if (!string.IsNullOrEmpty(id))
            {
                var manifest = ManagedUserscriptManager.Default.GetManifest(id);
                return manifest ?? (object)new { error = "NOT_FOUND", id };
            }
        }
        return ManagedUserscriptManager.Default.Describe();
    }

    private static object RpcUserscriptVerify(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var id = RequiredProperty(p, "id");
        var script = p.TryGetProperty("script", out var s) ? s.GetString() ?? "" : ManagedUserscriptManager.CanonicalChatLockScript;
        var manifest = ManagedUserscriptManager.Default.GetManifest(id) ?? new UserscriptManifest(
            Id: id,
            Name: id,
            Version: "1.0.0",
            Origin: ManagedUserscriptManager.CanonicalOrigin,
            RunAt: "document-start",
            ContentHash: ManagedUserscriptManager.ComputeSha256(script),
            Grants: new[] { "none" });

        return ManagedUserscriptManager.Default.VerifyAndRegister(manifest, script);
    }

    private static object CodexEgressStatus(string[] args)
    {
        var seed = Option(args, "--seed");
        return CodexEgressFlowOracle.Evaluate(seed);
    }

    private static object RpcCodexEgressStatus(JsonElement? parameters)
    {
        string? seed = null;
        if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object &&
            parameters.Value.TryGetProperty("seed", out var seedElem) && seedElem.ValueKind == JsonValueKind.String)
        {
            seed = seedElem.GetString();
        }
        return CodexEgressFlowOracle.Evaluate(seed);
    }

    private static async Task<object> AccessibilityPreferencesAsync()
    {
        var value = await AccessibilityPreferencesStore.LoadAsync();
        return new
        {
            schema = AccessibilityPreferencesStore.Schema,
            platform = PlatformName(),
            interface_scale = value.InterfaceScale,
            reduced_motion = value.ReducedMotion,
            minimum_scale = AccessibilityPreferencesStore.MinimumScale,
            maximum_scale = AccessibilityPreferencesStore.MaximumScale,
            scale_step = AccessibilityPreferencesStore.ScaleStep
        };
    }

    private static async Task<object> SetAccessibilityPreferencesAsync(string[] args)
    {
        var current = await AccessibilityPreferencesStore.LoadAsync();
        var scale = current.InterfaceScale;
        var reducedMotion = current.ReducedMotion;

        var scaleText = Option(args, "--scale");
        if (!string.IsNullOrWhiteSpace(scaleText))
        {
            if (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
                throw new MachineException(2, "INVALID_PARAMS", "--scale must be a number.");
        }

        var motionText = Option(args, "--reduced-motion");
        if (!string.IsNullOrWhiteSpace(motionText))
        {
            if (!bool.TryParse(motionText, out reducedMotion))
                throw new MachineException(2, "INVALID_PARAMS", "--reduced-motion must be true or false.");
        }

        var saved = await AccessibilityPreferencesStore.SaveAsync(scale, reducedMotion);
        return new
        {
            schema = AccessibilityPreferencesStore.Schema,
            platform = PlatformName(),
            interface_scale = saved.InterfaceScale,
            reduced_motion = saved.ReducedMotion
        };
    }

    private static object ReadCombinedCatalog() => new
    {
        schema = Schema,
        codex = ReadCatalog("codex-capability-catalog-20261001.json"),
        siwc = ReadCatalog("siwc-capability-surface-20261001.json")
    };

    private static JsonElement ReadCatalog(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "oracles", fileName);
        if (!File.Exists(path))
            throw new MachineException(4, "CATALOG_MISSING", $"Packaged capability catalog is missing: {fileName}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static async Task<object> DoctorAsync()
    {
        var credential = await CredentialStore.TryLoadAsync();
        var registrations = await CredentialStore.ListRegistrationsAsync();
        var codex = CodexAppServer.ResolveExecutable();

        return new
        {
            schema = Schema,
            platform = PlatformName(),
            app_state_directory = AppState.DirectoryPath,
            auth = CredentialSummary(credential),
            registration_count = registrations.Count,
            codex = new
            {
                resolved_executable = codex,
                bundled = Path.IsPathRooted(codex) && File.Exists(codex)
            }
        };
    }

    private static async Task<object> AuthStatusAsync() =>
        new
        {
            schema = Schema,
            auth = await Core.GetAuthStatusAsync()
        };

    private static async Task<object> AuthBundleInspectAsync(string[] args)
    {
        var input = RequiredOption(args, "--input");
        return new
        {
            schema = Schema,
            bundle = await Core.InspectCredentialBundleAsync(input)
        };
    }

    private static async Task<object> AuthExportAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Exporting renewable SIWC credentials");
        var output = RequiredOption(args, "--output");
        var passphrase = PassphraseFromEnvironment(args);
        try
        {
            return await Core.ExportCredentialAsync(output, passphrase);
        }
        finally
        {
            passphrase = string.Empty;
        }
    }

    private static async Task<object> AuthImportAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Importing renewable SIWC credentials");
        var input = RequiredOption(args, "--input");
        var passphrase = PassphraseFromEnvironment(args);
        try
        {
            return await Core.ImportCredentialAsync(input, passphrase);
        }
        finally
        {
            passphrase = string.Empty;
        }
    }

    private static async Task<object> ModelsAsync(string[]? args = null)
    {
        var requestedMode = Option(args ?? Array.Empty<string>(), "--auth-mode");
        var requestedOrder = Option(args ?? Array.Empty<string>(), "--auth-order");

        AuthPolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(requestedMode))
            policy = new AuthPolicy(new[] { AuthModes.NormalizeMode(requestedMode) });
        else if (!string.IsNullOrWhiteSpace(requestedOrder))
            policy = new AuthPolicy(AuthPolicy.NormalizeOrder(requestedOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

        var resolvedAuth = await Core.GetUsableAuthAsync(requirePlanUsage: false, policy: policy, requireDirectRestAccess: true);
        var models = await Core.ListModelsAsync(policy: policy);
        return new
        {
            schema = Schema,
            binding = "siwc-responses",
            auth_mode = resolvedAuth.AuthMode,
            models = models.Select(x => new { id = x.Slug, display_name = x.DisplayName }).ToArray()
        };
    }

    private static async Task<object> AuthPolicyAsync()
    {
        var policy = await AuthPolicyStore.LoadAsync();
        var report = await Core.GetAuthEvaluationAsync(policy);
        return new
        {
            schema = "suprachat-auth-policy/v1",
            configured_order = report.ConfiguredOrder,
            prefer_codex_auth_json = policy.PreferCodexAuthJson,
            custom_api_key_env = policy.CustomApiKeyEnv,
            active_mode = report.Resolved?.AuthMode,
            active_label = report.Resolved?.PrincipalLabel,
            has_plan_usage = report.Resolved?.HasPlanUsage ?? false,
            evaluations = report.Evaluations.Select(e => new
            {
                auth_mode = e.AuthMode,
                available = e.Available,
                reason = e.Reason,
                principal = e.PrincipalLabel
            }).ToArray()
        };
    }

    private static async Task<object> AuthPolicySetAsync(string[] args)
    {
        var orderStr = Option(args, "--order");
        var preferCodexStr = Option(args, "--prefer-codex");
        var customEnv = Option(args, "--custom-api-key-env");

        var current = await AuthPolicyStore.LoadAsync();
        var order = !string.IsNullOrWhiteSpace(orderStr)
            ? AuthPolicy.NormalizeOrder(orderStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : current.Order;

        var preferCodex = !string.IsNullOrWhiteSpace(preferCodexStr)
            ? bool.Parse(preferCodexStr)
            : current.PreferCodexAuthJson;

        var updated = current with
        {
            Order = order,
            PreferCodexAuthJson = preferCodex,
            CustomApiKeyEnv = customEnv ?? current.CustomApiKeyEnv
        };

        await AuthPolicyStore.SaveAsync(updated);
        return await AuthPolicyAsync();
    }

    private static async Task<object> AuthSetApiKeyAsync(string[] args)
    {
        var key = RequiredOption(args, "--key");
        await ApiKeyStore.SaveAsync(key);
        var cred = await ApiKeyStore.TryLoadAsync();
        return new
        {
            schema = Schema,
            operation = "set-api-key",
            status = "saved",
            key = cred?.RedactedKey,
            source = cred?.Source
        };
    }

    private static async Task<object> AuthClearApiKeyAsync()
    {
        ApiKeyStore.Clear();
        return new
        {
            schema = Schema,
            operation = "clear-api-key",
            status = "cleared"
        };
    }

    private static async Task<object> AuthClearAsync(string[] args)
    {
        SubscriptionCredentialStore.Clear();
        ApiKeyStore.Clear();
        CredentialStore.Clear();
        return new
        {
            schema = Schema,
            operation = "clear-all-auth",
            status = "cleared"
        };
    }

    private static async Task<object> AuthLoginAsync(string[] args)
    {
        var token = Option(args, "--token");
        var apiKey = Option(args, "--api-key");
        var isDeviceAuth = HasFlag(args, "--device-auth");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await ApiKeyStore.SaveAsync(apiKey);
            return new
            {
                schema = Schema,
                auth_mode = AuthModes.ApiKey,
                status = "saved",
                source = "protected-api-key"
            };
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            var cred = new SubscriptionCredential(token, Source: "direct-token");
            await SubscriptionCredentialStore.SaveAsync(cred);
            return new
            {
                schema = Schema,
                auth_mode = AuthModes.Subscription,
                status = "saved",
                source = "direct-token"
            };
        }

        if (isDeviceAuth)
        {
            return await RunDeviceAuthAsync();
        }

        var codexAuth = SubscriptionCredentialStore.TryLoadFromCodex();
        if (codexAuth is not null)
        {
            return new
            {
                schema = Schema,
                auth_mode = AuthModes.Subscription,
                status = "linked",
                source = "codex-auth-json",
                email = codexAuth.Email,
                account_id = codexAuth.AccountId,
                message = "Linked active ChatGPT subscription from ~/.codex/auth.json"
            };
        }

        throw new MachineException(2, "LOGIN_METHOD_REQUIRED",
            "No active Codex subscription found in ~/.codex/auth.json. Specify --device-auth for headless device code login, --token <access_token> to supply a token directly, or --api-key <key>.");
    }

    private static async Task<object> RunDeviceAuthAsync()
    {
        using var client = new HttpClient();
        var initContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "pdlLIX2Y72MIl2rhLhTE9VV9bN905kBh",
            ["scope"] = "openid profile email offline_access model.request",
            ["audience"] = "https://api.openai.com/v1"
        });

        using var initResp = await client.PostAsync("https://auth0.openai.com/oauth/device/code", initContent);
        if (!initResp.IsSuccessStatusCode)
        {
            var err = await initResp.Content.ReadAsStringAsync();
            throw new MachineException(3, "DEVICE_AUTH_FAILED", $"Failed to initiate device authentication: {err}");
        }

        var initJson = await initResp.Content.ReadAsStringAsync();
        using var initDoc = JsonDocument.Parse(initJson);
        var root = initDoc.RootElement;

        var deviceCode = root.GetProperty("device_code").GetString()!;
        var userCode = root.GetProperty("user_code").GetString()!;
        var verificationUri = root.GetProperty("verification_uri").GetString()!;
        var interval = root.TryGetProperty("interval", out var intProp) ? intProp.GetInt32() : 5;
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 900;

        Console.Error.WriteLine();
        Console.Error.WriteLine("=================================================================");
        Console.Error.WriteLine($"Open in your browser: {verificationUri}");
        Console.Error.WriteLine($"Enter one-time code:  {userCode}");
        Console.Error.WriteLine("Waiting for authorization...");
        Console.Error.WriteLine("=================================================================");
        Console.Error.WriteLine();

        var pollDeadline = DateTime.UtcNow.AddSeconds(expiresIn);
        while (DateTime.UtcNow < pollDeadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, interval)));

            var pollContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = "pdlLIX2Y72MIl2rhLhTE9VV9bN905kBh"
            });

            using var pollResp = await client.PostAsync("https://auth0.openai.com/oauth/token", pollContent);
            var pollBody = await pollResp.Content.ReadAsStringAsync();

            if (pollResp.IsSuccessStatusCode)
            {
                using var pollDoc = JsonDocument.Parse(pollBody);
                var pRoot = pollDoc.RootElement;
                var accessToken = pRoot.GetProperty("access_token").GetString()!;
                var refreshToken = pRoot.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                var idToken = pRoot.TryGetProperty("id_token", out var it) ? it.GetString() : null;

                var sub = new SubscriptionCredential(
                    accessToken,
                    refreshToken,
                    idToken,
                    ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(pRoot.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600),
                    Source: "device-auth");

                await SubscriptionCredentialStore.SaveAsync(sub);
                return new
                {
                    schema = Schema,
                    auth_mode = AuthModes.Subscription,
                    status = "authorized",
                    source = "device-auth",
                    expires_at = sub.ExpiresAt
                };
            }

            if (pollBody.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
                continue;

            if (pollBody.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval += 5;
                continue;
            }

            throw new MachineException(3, "DEVICE_AUTH_FAILED", $"Device auth polling error: {pollBody}");
        }

        throw new MachineException(3, "DEVICE_AUTH_EXPIRED", "Device authentication expired before user authorization was received.");
    }

    private static void RequireExplicitConfirmation(string[] args, string operation)
    {
        if (!HasFlag(args, "--confirm"))
            throw new MachineException(
                2,
                "CONFIRMATION_REQUIRED",
                $"{operation} is consequential. Repeat with --confirm after reviewing the requested action.");
    }

    private static async Task<object> RemoteEnableAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Enabling Codex Remote");
        var body = JsonSerializer.Serialize(new { ephemeral = HasFlag(args, "--ephemeral") });
        return await CodexRpcAsync(new[] { "--method", "remoteControl/enable", "--params", body });
    }

    private static async Task<object> RemoteDisableAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Disabling Codex Remote");
        var body = JsonSerializer.Serialize(new { ephemeral = HasFlag(args, "--ephemeral") });
        return await CodexRpcAsync(new[] { "--method", "remoteControl/disable", "--params", body });
    }

    private static async Task<object> RemotePairAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Starting Codex Remote pairing");
        var body = JsonSerializer.Serialize(new { manualCode = HasFlag(args, "--manual-code") });
        return await CodexRpcAsync(new[] { "--method", "remoteControl/pairing/start", "--params", body });
    }

    private static async Task<object> RemotePairStatusAsync(string[] args)
    {
        var pairingCode = Option(args, "--pairing-code");
        var manualCode = Option(args, "--manual-code");
        var body = JsonSerializer.Serialize(new
        {
            pairingCode,
            manualPairingCode = manualCode
        });
        return await CodexRpcAsync(new[] { "--method", "remoteControl/pairing/status", "--params", body });
    }

    private static async Task<object> RemoteClientsAsync(string[] args)
    {
        var environmentId = RequiredOption(args, "--environment");
        var body = JsonSerializer.Serialize(new
        {
            environmentId,
            cursor = (string?)null,
            limit = (int?)null,
            order = (string?)null
        });
        return await CodexRpcAsync(new[] { "--method", "remoteControl/client/list", "--params", body });
    }

    private static async Task<object> RemoteRevokeAsync(string[] args)
    {
        RequireExplicitConfirmation(args, "Revoking a Codex Remote client");
        var environmentId = RequiredOption(args, "--environment");
        var clientId = RequiredOption(args, "--client");
        var body = JsonSerializer.Serialize(new { environmentId, clientId });
        return await CodexRpcAsync(new[] { "--method", "remoteControl/client/revoke", "--params", body });
    }

    private static async Task<object> ScreenCaptureAsync(string[] args)
    {
        var output = RequiredOption(args, "--output");
        try
        {
            var path = await DesktopScreenCapture.CaptureAsync(output);
            return new
            {
                schema = DesktopScreenCapture.Schema,
                platform = PlatformName(),
                output_path = path,
                bytes = new FileInfo(path).Length,
                descriptor = DesktopScreenCapture.Describe()
            };
        }
        catch (Exception ex)
        {
            throw new MachineException(4, "SCREEN_CAPTURE_FAILED", SafeMessage(ex));
        }
    }

    private static async Task<object> RespondAsync(string[] args)
    {
        var model = RequiredOption(args, "--model");
        var input = RequiredOption(args, "--input");
        var webSearch = HasFlag(args, "--web-search");
        var filePaths = Options(args, "--file");
        IReadOnlyList<ResponseAttachment>? attachments = null;
        if (filePaths.Count > 0)
        {
            try
            {
                attachments = await AttachmentInputs.LoadPathsAsync(filePaths);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new MachineException(2, "ATTACHMENT_INVALID", SafeMessage(ex));
            }
        }

        var auth = await RequireAuthAsync(args, requireDirectRestAccess: true);

        var result = await TracedResponses.StreamAsync(
            auth.AccessToken,
            model,
            input,
            attachments,
            enableWebSearch: webSearch);

        if (!result.Completed)
            throw new MachineException(4, "INCOMPLETE", "Responses stream ended without response.completed.");

        return new
        {
            schema = Schema,
            binding = "siwc-responses",
            completed = result.Completed,
            request_id = result.RequestId,
            event_types = result.EventTypes,
            attachment_count = attachments?.Count ?? 0,
            output_text = result.Text
        };
    }

    private static async Task<object> RawResponsesAsync(string[] args, bool allowStandardInput = true)
    {
        var model = RequiredOption(args, "--model");
        var raw = Option(args, "--body");
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!allowStandardInput) throw new MachineException(2, "INVALID_PARAMS", "A nonempty --body is required in a persistent command session.");
            raw = await Console.In.ReadToEndAsync();
        }
        if (string.IsNullOrWhiteSpace(raw))
            throw new MachineException(2, "USAGE", "Provide --body <json> or JSON on stdin.");

        var auth = await RequireAuthAsync(args, requireDirectRestAccess: true);
        var result = await TracedResponses.StreamRawAsync(
            auth.AccessToken,
            model,
            raw);

        return new
        {
            schema = Schema,
            binding = "siwc-responses-raw",
            completed = result.Completed,
            request_id = result.RequestId,
            event_types = result.EventTypes,
            output_text = result.Text
        };
    }

    private static async Task<object> CodexRpcAsync(string[] args)
    {
        var method = RequiredOption(args, "--method");
        var rawParams = Option(args, "--params");
        var auth = await RequireAuthAsync(args);

        JsonElement? parameters = null;
        if (!string.IsNullOrWhiteSpace(rawParams))
        {
            using var document = JsonDocument.Parse(rawParams);
            parameters = document.RootElement.Clone();
        }

        var useNative = auth.AuthMode == AuthModes.Subscription && string.Equals(auth.Source, "codex-auth-json", StringComparison.Ordinal);
        await using var client = await CodexAppServerClient.StartAsync(auth.AccessToken, useNativeSubscription: useNative, trace: Trace.Binding, controls: Trace.Controls);
        var result = await client.RequestAsync(method, parameters);
        return new
        {
            schema = Schema,
            binding = "codex-app-server",
            method,
            result
        };
    }

    private static async Task<int> RunTraceSessionAsync()
    {
        var pending = new List<Task>();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            pending.RemoveAll(task => task.IsCompleted);
            var concurrent = false;
            try
            {
                using var request = JsonDocument.Parse(line);
                concurrent = request.RootElement.ValueKind == JsonValueKind.Object &&
                    request.RootElement.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String &&
                    operation.GetString() is "respond" or "responses-raw" or "codex-rpc";
            }
            catch (JsonException) { }
            if (concurrent)
            {
                if (pending.Count >= 8)
                    await WriteRpcLineAsync(new { ok = false, error = new { code = "BUSY", message = "Owned request capacity reached." } });
                else
                {
                    var commandId = Guid.NewGuid();
                    await WriteRpcLineAsync(new { ok = true, accepted = true, command_id = commandId });
                    pending.Add(ProcessTraceLineAsync(line, commandId));
                }
            }
            else await ProcessTraceLineAsync(line);
        }
        await Task.WhenAll(pending);
        return 0;
    }

    private static async Task ProcessTraceLineAsync(string line, Guid? commandId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var operation = root.GetProperty("operation").GetString();
            var parameters = root.TryGetProperty("params", out var p) ? p : (JsonElement?)null;
            var arguments = root.TryGetProperty("arguments", out var a)
                ? a.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : Array.Empty<string>();
            object result = operation switch
            {
                "status" or "start" or "stop" or "read" or "export" or "streams" or "control/preview" or "control/apply" => Trace.Execute(operation, parameters),
                "respond" => await RespondAsync(arguments),
                "responses-raw" when !string.IsNullOrWhiteSpace(Option(arguments, "--body")) => await RawResponsesAsync(arguments, allowStandardInput: false),
                "codex-rpc" => await CodexRpcAsync(arguments),
                _ => throw new UnauthorizedAccessException("Trace-session operation refused.")
            };
            await WriteRpcLineAsync(new { ok = true, command_id = commandId, value = result });
        }
        catch
        {
            await WriteRpcLineAsync(new { ok = false, command_id = commandId, error = new { code = "TRACE_COMMAND_REFUSED", message = "Local trace command failed or was refused." } });
        }

    }

    private static async Task<int> RunStdioAsync()
    {
        Console.Error.WriteLine("{\"suprachat\":\"stdio-ready\",\"schema\":\"suprachat-jsonrpc/v1\"}");
        var pending = new List<Task>();
        var brokerSession = new BrokerStdioSession(Core);
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                pending.RemoveAll(task => task.IsCompleted);
                var concurrent = false;
                try
                {
                    using var request = JsonDocument.Parse(line);
                    concurrent = request.RootElement.ValueKind == JsonValueKind.Object && request.RootElement.TryGetProperty("method", out var method) &&
                        method.ValueKind == JsonValueKind.String && method.GetString() is "responses/create" or "responses/raw";
                }
                catch (JsonException) { }
                if (concurrent)
                {
                    if (pending.Count >= 8)
                        await WriteRpcLineAsync(RpcError(line, -32000, "BUSY", "Owned request capacity reached."));
                    else pending.Add(ProcessRpcLineAsync(line, brokerSession));
                }
                else await ProcessRpcLineAsync(line, brokerSession);
            }
            await Task.WhenAll(pending);
        }
        finally
        {
            await StopAgentBrowserAsync();
            await StopAgentResponsesAsync();
            await StopAgentCodexAsync();
        }
        return 0;
    }

    private static async Task ProcessRpcLineAsync(string line, BrokerStdioSession brokerSession)
    {
        object response;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idValue) ? idValue.Clone() : default(JsonElement?);
            var method = root.TryGetProperty("method", out var methodValue)
                ? methodValue.GetString()
                : null;
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default(JsonElement?);

            if (string.IsNullOrWhiteSpace(method))
                throw new MachineException(2, "INVALID_REQUEST", "JSON-RPC method is required.");

            var result = await DispatchRpcAsync(method, parameters, brokerSession);
            response = new
            {
                jsonrpc = "2.0",
                id,
                result
            };
        }
        catch (MachineException ex)
        {
            response = RpcError(line, -32000, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            response = RpcError(line, -32700, "PARSE_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            response = RpcError(line, -32001, "FAILED", SafeMessage(ex));
        }

        await WriteRpcLineAsync(response);
    }

    private static async Task<object> DispatchRpcAsync(string method, JsonElement? parameters, BrokerStdioSession brokerSession)
    {
        return method switch
        {
            "session/ledger/read" => SharedSessionLedger.DescribeContract(),
            "session/ledger/list" => await RpcLedgerAsync(parameters, "list"),
            "session/ledger/open" => await RpcLedgerAsync(parameters, "open"),
            "session/ledger/change" => await RpcLedgerAsync(parameters, "change"),
            "session/ledger/revoke-own" => await RpcLedgerAsync(parameters, "revoke-own"),
            "session/draft/review" => await RpcSessionDraftReviewAsync(parameters),
            "session/draft/stage" => await RpcSessionDraftStageAsync(parameters),
            "session/draft/read" => await RpcSessionDraftReadAsync(parameters),
            "session/draft/discard" => await RpcSessionDraftDiscardAsync(parameters),
            "session/export" => await RpcSessionExportAsync(parameters),
            "session/status/set" => await RpcSessionStatusSetAsync(parameters),
            "session/close" => await RpcSessionCloseAsync(parameters),
            "session/restore" => await RpcSessionRestoreAsync(parameters),
            "capabilities/read" => Capabilities(),
            "accessibility/read" => Accessibility(),
            "accessibility/preferences/read" => await AccessibilityPreferencesAsync(),
            "accessibility/preferences/write" => await RpcAccessibilityPreferencesWriteAsync(parameters),
            "parity/read" => ReadCatalog("audience-parity-20261001.json"),
            "catalog/read" => ReadCombinedCatalog(),
            "codex/catalog" => ReadCatalog("codex-capability-catalog-20261001.json"),
            "siwc/catalog" => ReadCatalog("siwc-capability-surface-20261001.json"),
            "doctor/read" => await DoctorAsync(),
            "diagnostics/read" => DogfoodObservability.Describe(),
            "trace/status" => Trace.Execute("status", parameters),
            "trace/start" => Trace.Execute("start", parameters),
            "trace/stop" => Trace.Execute("stop", parameters),
            "trace/read" => Trace.Execute("read", parameters),
            "trace/export" => Trace.Execute("export", parameters),
            "trace/streams" => Trace.Execute("streams", parameters),
            "control/preview" => Trace.Execute("control/preview", parameters),
            "control/apply" => Trace.Execute("control/apply", parameters),
            "diagnostics/export" => await RpcDiagnosticsExportAsync(parameters),
            "auth/status" => await AuthStatusAsync(),
            "auth/bundle/inspect" => await RpcAuthBundleInspectAsync(parameters),
            "auth/export" => await RpcAuthExportAsync(parameters),
            "auth/import" => await RpcAuthImportAsync(parameters),
            "broker/status" => BrokerStatusView(await brokerSession.StatusAsync()),
            "lease/acquire" => await RpcBrokerAcquireAsync(brokerSession, parameters),
            "lease/status" => await RpcBrokerLeaseStatusAsync(brokerSession, parameters),
            "lease/release" => await RpcBrokerReleaseAsync(brokerSession, parameters),
            "models/list" => await RpcBrokerModelsAsync(brokerSession, parameters),
            "realtime/voices" => await RpcCodexReadAsync("thread/realtime/listVoices", emptyParams: true),
            "remote/status" => await RpcCodexReadAsync("remoteControl/status/read", emptyParams: false),
            "remote/enable" => await RpcRemoteEnableAsync(parameters),
            "remote/disable" => await RpcRemoteDisableAsync(parameters),
            "remote/pairing/start" => await RpcRemotePairAsync(parameters),
            "remote/pairing/status" => await RpcRemotePairStatusAsync(parameters),
            "remote/clients/list" => await RpcRemoteClientsAsync(parameters),
            "remote/clients/revoke" => await RpcRemoteRevokeAsync(parameters),
            "plugins/list" => await RpcCodexReadAsync("plugin/list", emptyParams: true),
            "permissions/profiles" => await RpcCodexReadAsync("permissionProfile/list", emptyParams: true),
            "apps/list" => await RpcCodexReadAsync("app/list", emptyParams: true),
            "sandbox/readiness" => await RpcCodexReadAsync("windowsSandbox/readiness", emptyParams: false),
            "screen/status" => DesktopScreenCapture.Describe(),
            "screen/capture" => await RpcScreenCaptureAsync(parameters),
            "browser/status" => BrowserStatus(),
            "browser/sensors/list" => BrowserCapabilityRegistry.Default.GetSensors(),
            "browser/actuators/list" => BrowserCapabilityRegistry.Default.GetActuators(),
            "browser/sensor/observe" => await RpcBrowserSensorObserveAsync(parameters),
            "browser/actuator/execute" => await RpcBrowserActuatorExecuteAsync(parameters),
            "browser/mode/preflight" => RpcBrowserModePreflight(parameters),
            "browser/trace/snapshot" => RpcBrowserTraceSnapshot(parameters),
            "browser/differential/evaluate" => RpcBrowserDifferentialEvaluate(parameters),
            "userscript/list" => ManagedUserscriptManager.Default.ListManifests(),
            "userscript/status" => RpcUserscriptStatus(parameters),
            "userscript/verify" => RpcUserscriptVerify(parameters),
            "codex/egress/status" => RpcCodexEgressStatus(parameters),
            "browser/snapshot" => await RpcBrowserSnapshotAsync(parameters),
            "browser/start" => await RpcBrowserStartAsync(),
            "browser/navigate" => await RpcBrowserNavigateAsync(parameters),
            "browser/read" => await RpcBrowserReadAsync(),
            "browser/click" => await RpcBrowserClickAsync(parameters),
            "browser/fill" => await RpcBrowserFillAsync(parameters),
            "browser/stop" => await RpcBrowserStopAsync(),
            "responses/create" => await RpcResponseCreateAsync(parameters),
            "responses/raw" => await RpcResponsesRawAsync(parameters),
            "responses/ws/connect" => await RpcResponsesConnectAsync(),
            "responses/ws/send" => await RpcResponsesSendAsync(parameters),
            "responses/ws/disconnect" => await RpcResponsesDisconnectAsync(),
            "codex/start" => await RpcCodexStartAsync(),
            "codex/request" => await RpcCodexRequestAsync(parameters),
            "codex/respond" => await RpcCodexRespondAsync(parameters),
            "codex/reject" => await RpcCodexRejectAsync(parameters),
            "codex/stop" => await RpcCodexStopAsync(),
            _ => throw new MachineException(2, "METHOD_NOT_FOUND", $"Unsupported method: {method}")
        };
    }

    private static async Task<object> DiagnosticsExportAsync(string[] args)
    {
        var output = RequiredOption(args, "--output");
        var operation = DogfoodObservability.BeginOperation("diagnostics-export");
        await DogfoodObservability.RecordOperationAsync(
            "diagnostics",
            "export",
            "start",
            operation);

        var path = await DogfoodObservability.ExportAsync(output);
        await DogfoodObservability.RecordOperationAsync(
            "diagnostics",
            "export",
            "success",
            operation,
            new Dictionary<string, object?>
            {
                ["file_name"] = Path.GetFileName(path),
                ["size_bytes"] = new FileInfo(path).Length
            });

        return new
        {
            schema = "suprachat-dogfood-diagnostics-export/v2",
            path,
            observability = DogfoodObservability.Describe()
        };
    }

    private static object BrokerStatusView(BrokerStatus status) => new
    {
        schema = "suprachat-broker-status/v1",
        host_id = status.HostId,
        epoch = status.Epoch,
        credential_generation = status.CredentialGeneration,
        credential_present = status.CredentialPresent,
        refresh_in_progress = status.RefreshInProgress,
        active_lease_count = status.ActiveLeaseCount
    };

    private static object? BrokerLeaseViewObject(BrokerLeaseView? lease) =>
        lease is null
            ? null
            : new
            {
                lease_id = lease.LeaseId,
                consumer_id = lease.ConsumerId,
                capabilities = lease.Capabilities,
                issued_at = lease.IssuedAt,
                expires_at = lease.ExpiresAt,
                epoch = lease.Epoch,
                credential_generation = lease.CredentialGeneration,
                state = lease.State.ToString().ToUpperInvariant()
            };

    private static object BrokerAcquireView(BrokerAcquireResult result) => new
    {
        schema = "suprachat-broker-acquire/v1",
        request_id = result.RequestId,
        classification = result.Classification,
        lease = BrokerLeaseViewObject(result.Lease),
        error = result.Error,
        replay = result.Replay
    };

    private static object BrokerReleaseView(BrokerReleaseResult result) => new
    {
        schema = "suprachat-broker-release/v1",
        request_id = result.RequestId,
        classification = result.Classification,
        lease_id = result.LeaseId,
        lease_state = result.LeaseState?.ToString().ToUpperInvariant(),
        error = result.Error,
        replay = result.Replay
    };

    private static async Task<object> RpcBrokerAcquireAsync(
        BrokerStdioSession brokerSession,
        JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var requestId = RequiredProperty(p, "request_id");

        if (!p.TryGetProperty("capabilities", out var capabilitiesValue) ||
            capabilitiesValue.ValueKind != JsonValueKind.Array)
            throw new MachineException(2, "INVALID_PARAMS", "params.capabilities must be a non-empty array of strings.");

        var capabilities = new List<string>();
        foreach (var value in capabilitiesValue.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new MachineException(2, "INVALID_PARAMS", "params.capabilities entries must be non-empty strings.");
            capabilities.Add(value.GetString()!);
        }

        if (capabilities.Count == 0)
            throw new MachineException(2, "INVALID_PARAMS", "params.capabilities must be non-empty.");

        TimeSpan? ttl = null;
        if (p.TryGetProperty("ttl_seconds", out var ttlValue))
        {
            if (ttlValue.ValueKind != JsonValueKind.Number ||
                !ttlValue.TryGetInt32(out var ttlSeconds) ||
                ttlSeconds <= 0)
                throw new MachineException(2, "INVALID_PARAMS", "params.ttl_seconds must be a positive integer.");
            ttl = TimeSpan.FromSeconds(ttlSeconds);
        }

        return BrokerAcquireView(
            await brokerSession.AcquireAsync(requestId, capabilities, ttl).ConfigureAwait(false));
    }

    private static async Task<object> RpcBrokerLeaseStatusAsync(
        BrokerStdioSession brokerSession,
        JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var leaseId = RequiredProperty(p, "lease_id");
        var lease = await brokerSession.LeaseStatusAsync(leaseId).ConfigureAwait(false);
        return new
        {
            schema = "suprachat-broker-lease-status/v1",
            lease_id = leaseId,
            lease = BrokerLeaseViewObject(lease),
            error = lease is null ? BrokerErrorCodes.LeaseNotFound : null
        };
    }

    private static async Task<object> RpcBrokerReleaseAsync(
        BrokerStdioSession brokerSession,
        JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        return BrokerReleaseView(
            await brokerSession.ReleaseAsync(
                RequiredProperty(p, "request_id"),
                RequiredProperty(p, "lease_id")).ConfigureAwait(false));
    }

    private static async Task<object> RpcBrokerModelsAsync(
        BrokerStdioSession brokerSession,
        JsonElement? parameters)
    {
        if (parameters is null)
            return await ModelsAsync().ConfigureAwait(false);

        var p = RequireObject(parameters);
        var leaseId = OptionalString(p, "lease_id");
        if (leaseId is null)
            return await ModelsAsync().ConfigureAwait(false);

        var operationId = RequiredProperty(p, "operation_id");
        var admission = await brokerSession
            .AdmitAsync(operationId, leaseId, "models.read")
            .ConfigureAwait(false);
        if (admission.Error is not null || admission.Admission is null)
            throw new MachineException(
                3,
                admission.Error ?? "BROKER_UNAVAILABLE",
                "The broker did not admit model discovery for this lease.");

        var result = await ModelsAsync().ConfigureAwait(false);
        var deliveryError = await brokerSession
            .ResultDeliveryErrorAsync(admission.Admission)
            .ConfigureAwait(false);
        if (deliveryError is not null)
            throw new MachineException(
                3,
                deliveryError,
                "The broker no longer permits delivery of the admitted model-discovery result.");

        return result;
    }

    private static async Task<object> RpcDiagnosticsExportAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        return await DiagnosticsExportAsync(new[]
        {
            "--output",
            RequiredProperty(p, "output")
        });
    }

    private static async Task<object> RpcAuthBundleInspectAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        return await AuthBundleInspectAsync(new[]
        {
            "--input",
            RequiredProperty(p, "input")
        });
    }

    private static async Task<object> RpcAuthExportAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Exporting renewable SIWC credentials");
        return await Core.ExportCredentialAsync(
            RequiredProperty(p, "output"),
            PassphraseFromEnvironmentName(RequiredProperty(p, "passphrase_env")));
    }

    private static async Task<object> RpcAuthImportAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Importing renewable SIWC credentials");
        return await Core.ImportCredentialAsync(
            RequiredProperty(p, "input"),
            PassphraseFromEnvironmentName(RequiredProperty(p, "passphrase_env")));
    }

    private static async Task<object> RpcRemoteEnableAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Enabling Codex Remote");
        var args = new List<string> { "--confirm" };
        if (OptionalBoolean(p, "ephemeral"))
            args.Add("--ephemeral");
        return await RemoteEnableAsync(args.ToArray());
    }

    private static async Task<object> RpcRemoteDisableAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Disabling Codex Remote");
        var args = new List<string> { "--confirm" };
        if (OptionalBoolean(p, "ephemeral"))
            args.Add("--ephemeral");
        return await RemoteDisableAsync(args.ToArray());
    }

    private static async Task<object> RpcRemotePairAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Starting Codex Remote pairing");
        var args = new List<string> { "--confirm" };
        if (OptionalBoolean(p, "manual_code"))
            args.Add("--manual-code");
        return await RemotePairAsync(args.ToArray());
    }

    private static async Task<object> RpcRemotePairStatusAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var args = new List<string>();
        var pairingCode = OptionalString(p, "pairing_code");
        var manualCode = OptionalString(p, "manual_code");
        if (pairingCode is not null)
        {
            args.Add("--pairing-code");
            args.Add(pairingCode);
        }
        if (manualCode is not null)
        {
            args.Add("--manual-code");
            args.Add(manualCode);
        }
        return await RemotePairStatusAsync(args.ToArray());
    }

    private static async Task<object> RpcRemoteClientsAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var environment = RequiredProperty(p, "environment");
        return await RemoteClientsAsync(new[] { "--environment", environment });
    }

    private static async Task<object> RpcRemoteRevokeAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Revoking a Codex Remote client");
        var environment = RequiredProperty(p, "environment");
        var client = RequiredProperty(p, "client");
        return await RemoteRevokeAsync(new[] { "--confirm", "--environment", environment, "--client", client });
    }

    private static async Task<object> RpcScreenCaptureAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var output = RequiredProperty(p, "output_path");
        return await ScreenCaptureAsync(new[] { "--output", output });
    }

    private static async Task<object> RpcAccessibilityPreferencesWriteAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var current = await AccessibilityPreferencesStore.LoadAsync();
        var scale = current.InterfaceScale;
        var reducedMotion = current.ReducedMotion;

        if (p.TryGetProperty("interface_scale", out var scaleValue))
        {
            if (scaleValue.ValueKind != JsonValueKind.Number || !scaleValue.TryGetDouble(out scale))
                throw new MachineException(2, "INVALID_PARAMS", "interface_scale must be a number.");
        }

        if (p.TryGetProperty("reduced_motion", out var motionValue))
        {
            if (motionValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new MachineException(2, "INVALID_PARAMS", "reduced_motion must be a boolean.");
            reducedMotion = motionValue.GetBoolean();
        }

        var saved = await AccessibilityPreferencesStore.SaveAsync(scale, reducedMotion);
        return new
        {
            schema = AccessibilityPreferencesStore.Schema,
            platform = PlatformName(),
            interface_scale = saved.InterfaceScale,
            reduced_motion = saved.ReducedMotion
        };
    }

    private static async Task<object> RpcBrowserSnapshotAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var url = RequiredProperty(p, "url");
        return await BrowserSnapshotAsync(new[] { "--url", url });
    }

    private static async Task<object> RpcBrowserStartAsync()
    {
        if (_agentBrowser is null)
            _agentBrowser = await BrowserSession.StartAsync(headless: true);

        return new
        {
            schema = BrowserSession.Schema,
            started = true,
            url = _agentBrowser.Page.Url,
            isolated = true,
            headless = true
        };
    }

    private static async Task<object> RpcBrowserNavigateAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var url = RequiredProperty(p, "url");
        var browser = RequireAgentBrowser();
        return await browser.NavigateAndSnapshotAsync(url);
    }

    private static async Task<object> RpcBrowserReadAsync()
    {
        var browser = RequireAgentBrowser();
        return await browser.SnapshotAsync();
    }

    private static async Task<object> RpcBrowserClickAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Browser click");
        var role = RequiredProperty(p, "role");
        var name = RequiredProperty(p, "name");
        return await RequireAgentBrowser().ClickByRoleAsync(role, name);
    }

    private static async Task<object> RpcBrowserFillAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        RequireRpcConfirmation(p, "Browser fill");
        var label = RequiredProperty(p, "label");
        var value = RequiredProperty(p, "value");
        return await RequireAgentBrowser().FillByLabelAsync(label, value);
    }

    private static async Task<object> RpcBrowserStopAsync()
    {
        var wasRunning = _agentBrowser is not null;
        await StopAgentBrowserAsync();
        return new
        {
            schema = BrowserSession.Schema,
            stopped = wasRunning
        };
    }

    private static BrowserSession RequireAgentBrowser() =>
        _agentBrowser ?? throw new MachineException(
            4,
            "BROWSER_NOT_STARTED",
            "Start a browser session with browser/start first.");

    private static async Task StopAgentBrowserAsync()
    {
        if (_agentBrowser is null)
            return;

        await _agentBrowser.DisposeAsync();
        _agentBrowser = null;
    }

    private static async Task<object> RpcResponseCreateAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var model = RequiredProperty(p, "model");
        var input = RequiredProperty(p, "input");
        var webSearch = p.TryGetProperty("web_search", out var ws) && ws.ValueKind == JsonValueKind.True;
        var args = new List<string>
        {
            "--model", model,
            "--input", input
        };

        if (p.TryGetProperty("files", out var files))
        {
            if (files.ValueKind != JsonValueKind.Array)
                throw new MachineException(2, "INVALID_PARAMS", "responses/create params.files must be an array of local file paths.");

            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new MachineException(2, "INVALID_PARAMS", "responses/create params.files entries must be non-empty strings.");
                args.Add("--file");
                args.Add(item.GetString()!);
            }
        }

        if (webSearch)
            args.Add("--web-search");

        return await RespondAsync(args.ToArray());
    }

    private static async Task<object> RpcResponsesRawAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var model = RequiredProperty(p, "model");
        if (!p.TryGetProperty("body", out var body))
            throw new MachineException(2, "INVALID_PARAMS", "responses/raw requires params.body.");

        return await RawResponsesAsync(new[]
        {
            "--model", model,
            "--body", body.GetRawText()
        });
    }

    private static async Task<object> RpcResponsesConnectAsync()
    {
        var socket = await EnsureAgentResponsesAsync();
        return new
        {
            schema = Schema,
            binding = "responses-websocket",
            connected = socket.IsConnected,
            endpoint = ResponsesWebSocketClient.Endpoint.AbsoluteUri
        };
    }

    private static async Task<object> RpcResponsesSendAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var model = RequiredProperty(p, "model");
        if (!p.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            throw new MachineException(2, "INVALID_PARAMS", "responses/ws/send requires object params.event.");

        var socket = await EnsureAgentResponsesAsync();
        await socket.SendRawAsync(evt.GetRawText(), model);
        return new
        {
            schema = Schema,
            binding = "responses-websocket",
            sent = true
        };
    }

    private static async Task<object> RpcResponsesDisconnectAsync()
    {
        var wasConnected = _agentResponsesSocket is { IsConnected: true };
        await StopAgentResponsesAsync();
        return new { schema = Schema, disconnected = wasConnected };
    }

    private static async Task<ResponsesWebSocketClient> EnsureAgentResponsesAsync()
    {
        if (_agentResponsesSocket is { IsConnected: true })
            return _agentResponsesSocket;

        await StopAgentResponsesAsync();
        var auth = await RequireAuthAsync(requireDirectRestAccess: true);
        _agentResponsesSocket = await ResponsesWebSocketClient.ConnectAsync(auth.AccessToken, trace: Trace.Binding, controls: Trace.Controls);
        _agentResponsesCts = new CancellationTokenSource();
        var socket = _agentResponsesSocket;
        var token = _agentResponsesCts.Token;
        _agentResponsesPump = Task.Run(() => PumpResponsesEventsAsync(socket, token), token);
        return socket;
    }

    private static async Task PumpResponsesEventsAsync(
        ResponsesWebSocketClient socket,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var raw in socket.ReadEventsAsync(cancellationToken))
            {
                object payload;
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    payload = document.RootElement.Clone();
                }
                catch
                {
                    payload = new { raw };
                }

                await WriteRpcLineAsync(new
                {
                    jsonrpc = "2.0",
                    method = "responses/ws/event",
                    @params = payload
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await WriteRpcLineAsync(new
            {
                jsonrpc = "2.0",
                method = "responses/ws/event",
                @params = new
                {
                    type = "monitor-error",
                    message = SafeMessage(ex)
                }
            });
        }
    }

    private static async Task StopAgentResponsesAsync()
    {
        var pump = _agentResponsesPump;
        _agentResponsesPump = null;

        _agentResponsesCts?.Cancel();
        _agentResponsesCts?.Dispose();
        _agentResponsesCts = null;

        if (_agentResponsesSocket is not null)
        {
            await _agentResponsesSocket.DisposeAsync();
            _agentResponsesSocket = null;
        }

        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch
            {
            }
        }
    }

    private static async Task<object> RpcCodexReadAsync(string method, bool emptyParams)
    {
        var client = await EnsureAgentCodexAsync();
        JsonElement? requestParams = emptyParams
            ? JsonSerializer.SerializeToElement(new { })
            : null;
        var result = await client.RequestAsync(method, requestParams);
        return new
        {
            schema = Schema,
            binding = "codex-app-server",
            method,
            read_only = true,
            result
        };
    }

    private static async Task<object> RpcCodexStartAsync()
    {
        var client = await EnsureAgentCodexAsync();
        return new
        {
            schema = Schema,
            binding = "codex-app-server",
            running = client.IsRunning,
            process_id = client.ProcessId
        };
    }

    private static async Task<object> RpcCodexRequestAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var method = RequiredProperty(p, "method");
        JsonElement? requestParams = p.TryGetProperty("params", out var rpcParams)
            ? rpcParams.Clone()
            : null;

        var client = await EnsureAgentCodexAsync();
        var result = await client.RequestAsync(method, requestParams);
        return new
        {
            schema = Schema,
            binding = "codex-app-server",
            method,
            result
        };
    }

    private static async Task<object> RpcCodexRespondAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var id = RequiredProperty(p, "id");
        var result = p.TryGetProperty("result", out var r)
            ? r.Clone()
            : JsonSerializer.SerializeToElement(new { });

        var client = await EnsureAgentCodexAsync();
        await client.RespondAsync(id, result);
        return new { schema = Schema, responded = true, id };
    }

    private static async Task<object> RpcCodexRejectAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var id = RequiredProperty(p, "id");
        var code = p.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : -32000;
        var message = p.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "Rejected by agent client."
            : "Rejected by agent client.";

        var client = await EnsureAgentCodexAsync();
        await client.RespondErrorAsync(id, code, message);
        return new { schema = Schema, rejected = true, id, code };
    }

    private static async Task<object> RpcCodexStopAsync()
    {
        var wasRunning = _agentCodex is { IsRunning: true };
        await StopAgentCodexAsync();
        return new { schema = Schema, stopped = wasRunning };
    }

    private static async Task<CodexAppServerClient> EnsureAgentCodexAsync()
    {
        if (_agentCodex is { IsRunning: true })
            return _agentCodex;

        await StopAgentCodexAsync();
        var auth = await RequireAuthAsync();
        var useNative = auth.AuthMode == AuthModes.Subscription && string.Equals(auth.Source, "codex-auth-json", StringComparison.Ordinal);
        _agentCodex = await CodexAppServerClient.StartAsync(auth.AccessToken, useNativeSubscription: useNative, trace: Trace.Binding, controls: Trace.Controls);
        _agentCodexCts = new CancellationTokenSource();
        _agentCodexEvents = _agentCodex.SubscribeEvents();
        var subscription = _agentCodexEvents;
        var token = _agentCodexCts.Token;
        _agentCodexPump = Task.Run(() => PumpCodexEventsAsync(subscription, token), token);
        return _agentCodex;
    }

    private static async Task PumpCodexEventsAsync(
        CodexAppServerClient.CodexEventSubscription subscription,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await WriteRpcLineAsync(new
                {
                    jsonrpc = "2.0",
                    method = "codex/event",
                    @params = new
                    {
                        kind = evt.Kind,
                        id = evt.Id,
                        method = evt.Method,
                        payload = evt.Payload
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await WriteRpcLineAsync(new
            {
                jsonrpc = "2.0",
                method = "codex/event",
                @params = new
                {
                    kind = "monitor-error",
                    message = SafeMessage(ex)
                }
            });
        }
    }

    private static async Task StopAgentCodexAsync()
    {
        var pump = _agentCodexPump;
        _agentCodexPump = null;

        _agentCodexCts?.Cancel();
        _agentCodexCts?.Dispose();
        _agentCodexCts = null;

        if (_agentCodexEvents is not null)
        {
            await _agentCodexEvents.DisposeAsync();
            _agentCodexEvents = null;
        }

        if (_agentCodex is not null)
        {
            await _agentCodex.DisposeAsync();
            _agentCodex = null;
        }

        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch
            {
            }
        }
    }

    private static object RpcError(string line, int code, string type, string message)
    {
        JsonElement? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var value))
                id = value.Clone();
        }
        catch
        {
        }

        return new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message,
                data = new { type }
            }
        };
    }

    private static async Task<ResolvedAuthCredential> RequireAuthAsync(string[]? args = null, bool requirePlanUsage = false, bool requireDirectRestAccess = false)
    {
        var requestedMode = Option(args ?? Array.Empty<string>(), "--auth-mode");
        var requestedOrder = Option(args ?? Array.Empty<string>(), "--auth-order");

        AuthPolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(requestedMode))
        {
            policy = new AuthPolicy(new[] { AuthModes.NormalizeMode(requestedMode) });
        }
        else if (!string.IsNullOrWhiteSpace(requestedOrder))
        {
            var parts = requestedOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy = new AuthPolicy(AuthPolicy.NormalizeOrder(parts));
        }

        try
        {
            return await Core.GetUsableAuthAsync(requirePlanUsage: requirePlanUsage, policy: policy, requireDirectRestAccess: requireDirectRestAccess);
        }
        catch (AuthResolutionException ex)
        {
            throw new MachineException(3, "AUTH_REQUIRED", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new MachineException(3, "AUTH_REQUIRED", ex.Message);
        }
    }

    private static async Task<ResolvedAuthCredential> RequireCredentialAsync(string[]? args = null) =>
        await RequireAuthAsync(args);

    private static object CredentialSummary(SiwcCredential? credential) =>
        SupraChatCore.DescribeCredential(credential);

    private static string PassphraseFromEnvironment(string[] args) =>
        PassphraseFromEnvironmentName(RequiredOption(args, "--passphrase-env"));

    private static string PassphraseFromEnvironmentName(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            throw new MachineException(2, "USAGE", "Passphrase environment variable name is required.");

        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
            throw new MachineException(
                3,
                "PASSPHRASE_REQUIRED",
                $"Environment variable {variableName} is not set or is empty.");
        return value;
    }

    private static void RequireRpcConfirmation(JsonElement parameters, string operation)
    {
        if (!parameters.TryGetProperty("confirm", out var confirm) ||
            confirm.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !confirm.GetBoolean())
            throw new MachineException(
                2,
                "CONFIRMATION_REQUIRED",
                $"{operation} is consequential. Repeat with params.confirm=true after reviewing the requested action.");
    }

    private static bool OptionalBoolean(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value))
            return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new MachineException(2, "INVALID_PARAMS", $"params.{name} must be a boolean.");
        return value.GetBoolean();
    }

    private static string? OptionalString(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new MachineException(2, "INVALID_PARAMS", $"params.{name} must be a string.");
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static JsonElement RequireObject(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
            throw new MachineException(2, "INVALID_PARAMS", "JSON-RPC params must be an object.");
        return value;
    }

    private static string RequiredProperty(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new MachineException(2, "INVALID_PARAMS", $"Missing string params.{name}.");

        return property.GetString()!;
    }

    private static string RequiredOption(string[] args, string name) =>
        Option(args, name)
        ?? throw new MachineException(2, "USAGE", $"Missing required option {name}.");

    // Reused from the previously qualified W6 experiment. No automatic owner
    // startup, credential provider, browser interaction, or remote binding.
    private static int ParseLedgerPort(string? input)
    {
        if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535)
            throw new MachineException(2, "INVALID_PARAMS", "Explicit synthetic loopback port must be 1024..65535.");
        return port;
    }

    private static async Task<int> RunSessionLedgerHostAsync(string[] args)
    {
        await SessionLedgerHost.RunAsync(ParseLedgerPort(Option(args, "--port")));
        return 0;
    }

    private static async Task<JsonElement> ReadLedgerStdinAsync()
    {
        var input = await Console.In.ReadToEndAsync();
        if (input.Length is 0 or > 8192)
            throw new MachineException(2, "INVALID_PARAMS", "Synthetic metadata JSON must be 1..8192 characters.");
        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new MachineException(2, "INVALID_PARAMS", "Metadata request must be a JSON object.");
        return document.RootElement.Clone();
    }

    private static async Task<object> RpcLedgerAsync(JsonElement? parameters, string operation)
    {
        var p = RequireObject(parameters);
        if (!p.TryGetProperty("port", out var el) ||
            el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var port))
            throw new MachineException(2, "INVALID_PARAMS", "Ledger params.port integer required.");
        JsonElement? request = null;
        if (operation != "list")
        {
            if (!p.TryGetProperty("request", out var body) || body.ValueKind != JsonValueKind.Object)
                throw new MachineException(2, "INVALID_PARAMS", "Ledger params.request JSON object required.");
            request = body;
        }
        return await SessionLedgerClient.RequestAsync(
            ParseLedgerPort(port.ToString(CultureInfo.InvariantCulture)), operation, request);
    }

    private static string DraftStorePath =>
        Environment.GetEnvironmentVariable("SUPRACHAT_DRAFTS_STORE")
        ?? Path.Combine(AppState.DirectoryPath, "local-drafts.json");

    private static IWorkbenchSessionAuthority ResolveAuthority(string? portString)
    {
        if (!string.IsNullOrWhiteSpace(portString))
        {
            var port = ParseLedgerPort(portString);
            return new W6SyntheticWorkbenchSessionAuthority(port);
        }
        var envPort = Environment.GetEnvironmentVariable("SUPRACHAT_W6_GUI_PORT");
        if (!string.IsNullOrWhiteSpace(envPort) &&
            int.TryParse(envPort, NumberStyles.None, CultureInfo.InvariantCulture, out var p) &&
            p is >= 1024 and <= 65535)
        {
            return new W6SyntheticWorkbenchSessionAuthority(p);
        }
        return new ExperimentalLocalWorkbenchSessionAuthority();
    }

    private static readonly JsonSerializerOptions DraftStoreOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static async Task<LedgerLocalDrafts> LoadDraftsAsync(IWorkbenchSessionAuthority authority)
    {
        var drafts = new LedgerLocalDrafts(authority);
        var path = DraftStorePath;
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path);
                var snapshot = JsonSerializer.Deserialize<LedgerDraftSnapshot>(json, DraftStoreOptions);
                if (snapshot is not null)
                    drafts.RestoreSnapshot(snapshot);
            }
            catch
            {
                // Unreadable or corrupt local draft store is safely ignored.
            }
        }
        return drafts;
    }

    private static async Task SaveDraftsAsync(LedgerLocalDrafts drafts)
    {
        var path = DraftStorePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var snapshot = drafts.ExportSnapshot();
        var json = JsonSerializer.Serialize(snapshot, DraftStoreOptions);
        await File.WriteAllTextAsync(path, json);
    }

    private static async Task<object> SessionDraftReviewAsync(string[] args)
    {
        var sessionId = RequiredOption(args, "--session");
        var direction = RequiredOption(args, "--direction");
        var text = Option(args, "--text");
        if (text is null && (HasFlag(args, "--stdin") || !Console.IsInputRedirected))
            text = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
            throw new MachineException(2, "INVALID_PARAMS", "Draft text is required via --text or standard input.");

        var authority = ResolveAuthority(Option(args, "--port"));
        using var drafts = await LoadDraftsAsync(authority);
        try
        {
            await drafts.SelectAsync(sessionId);
            var review = await drafts.PrepareAsync(direction, text);
            await SaveDraftsAsync(drafts);
            return review;
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new MachineException(3, "OPERATION_REFUSED", ex.Message);
        }
    }

    private static async Task<object> SessionDraftStageAsync(string[] args)
    {
        var sessionId = RequiredOption(args, "--session");
        var reviewId = RequiredOption(args, "--review-id");
        var confirmText = Option(args, "--confirm-text");
        if (confirmText is null && (HasFlag(args, "--stdin") || !Console.IsInputRedirected))
            confirmText = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(confirmText))
            throw new MachineException(2, "INVALID_PARAMS", "Confirm text is required via --confirm-text or standard input.");

        var authority = ResolveAuthority(Option(args, "--port"));
        using var drafts = await LoadDraftsAsync(authority);
        try
        {
            await drafts.SelectAsync(sessionId);
            var receipt = await drafts.StageReviewedAsync(reviewId, confirmText);
            await SaveDraftsAsync(drafts);
            return receipt;
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new MachineException(3, "OPERATION_REFUSED", ex.Message);
        }
    }

    private static async Task<object> SessionDraftReadAsync(string[] args)
    {
        var sessionId = RequiredOption(args, "--session");
        var authority = ResolveAuthority(Option(args, "--port"));
        using var drafts = await LoadDraftsAsync(authority);
        try
        {
            await drafts.SelectAsync(sessionId);
            var buffers = drafts.ReadSelected();
            return new
            {
                schema = LedgerLocalDrafts.Schema,
                session_id = sessionId,
                terminal_input = buffers.TerminalInput,
                chat_input = buffers.ChatInput
            };
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new MachineException(3, "OPERATION_REFUSED", ex.Message);
        }
    }

    private static async Task<object> SessionDraftDiscardAsync(string[] args)
    {
        var sessionId = Option(args, "--session");
        var authority = ResolveAuthority(Option(args, "--port"));
        using var drafts = await LoadDraftsAsync(authority);
        if (sessionId is not null)
            drafts.Forget(sessionId);
        else
            drafts.DiscardPending();
        await SaveDraftsAsync(drafts);
        return new
        {
            schema = LedgerLocalDrafts.Schema,
            session_id = sessionId,
            discarded = true
        };
    }

    private static async Task<object> SessionExportAsync(string[] args)
    {
        var format = RequiredOption(args, "--format");
        var output = Option(args, "--output");
        var sessionId = Option(args, "--session");
        var authority = ResolveAuthority(Option(args, "--port"));

        var receipt = await authority.ReadAsync(sessionId);
        if (receipt.Status != "OBSERVED" || receipt.Snapshot is null)
            throw new MachineException(4, "EXPORT_REFUSED", $"Export refused: {receipt.Status}");

        try
        {
            var preview = WorkbenchMetadataExport.Prepare(receipt.Snapshot, format);
            string? outputPath = null;
            if (!string.IsNullOrWhiteSpace(output))
            {
                outputPath = Path.GetFullPath(output);
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(outputPath, preview.Content, System.Text.Encoding.UTF8);
                preview = preview with { WasSaved = true };
            }

            return new
            {
                schema = preview.Schema,
                format = preview.Format,
                content = preview.Content,
                byte_count = preview.ContentBytes,
                was_saved = preview.WasSaved,
                requires_user_selected_destination = preview.RequiresUserSelectedDestination,
                output_path = outputPath,
                warning = preview.DataWarning
            };
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
    }

    private static async Task<object> RpcSessionDraftReviewAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var sessionId = RequiredProperty(p, "sessionId");
        var direction = RequiredProperty(p, "direction");
        var text = RequiredProperty(p, "text");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;

        var authority = ResolveAuthority(portStr);
        using var drafts = await LoadDraftsAsync(authority);
        try
        {
            await drafts.SelectAsync(sessionId);
            var review = await drafts.PrepareAsync(direction, text);
            await SaveDraftsAsync(drafts);
            return review;
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new MachineException(3, "OPERATION_REFUSED", ex.Message);
        }
    }

    private static async Task<object> RpcSessionDraftStageAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var sessionId = RequiredProperty(p, "sessionId");
        var reviewId = RequiredProperty(p, "reviewId");
        var confirmText = RequiredProperty(p, "confirmText");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;

        var authority = ResolveAuthority(portStr);
        using var drafts = await LoadDraftsAsync(authority);
        try
        {
            await drafts.SelectAsync(sessionId);
            var receipt = await drafts.StageReviewedAsync(reviewId, confirmText);
            await SaveDraftsAsync(drafts);
            return receipt;
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new MachineException(3, "OPERATION_REFUSED", ex.Message);
        }
    }

    private static async Task<object> RpcSessionDraftReadAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var sessionId = RequiredProperty(p, "sessionId");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;

        var authority = ResolveAuthority(portStr);
        using var drafts = await LoadDraftsAsync(authority);
        try
        {
            await drafts.SelectAsync(sessionId);
            var buffers = drafts.ReadSelected();
            return new
            {
                schema = LedgerLocalDrafts.Schema,
                session_id = sessionId,
                terminal_input = buffers.TerminalInput,
                chat_input = buffers.ChatInput
            };
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new MachineException(3, "OPERATION_REFUSED", ex.Message);
        }
    }

    private static async Task<object> RpcSessionDraftDiscardAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var sessionId = OptionalString(p, "sessionId");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;

        var authority = ResolveAuthority(portStr);
        using var drafts = await LoadDraftsAsync(authority);
        if (sessionId is not null)
            drafts.Forget(sessionId);
        else
            drafts.DiscardPending();
        await SaveDraftsAsync(drafts);
        return new
        {
            schema = LedgerLocalDrafts.Schema,
            session_id = sessionId,
            discarded = true
        };
    }

    private static async Task<object> RpcSessionExportAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var format = RequiredProperty(p, "format");
        var output = OptionalString(p, "output");
        var sessionId = OptionalString(p, "sessionId");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;

        var authority = ResolveAuthority(portStr);
        var receipt = await authority.ReadAsync(sessionId);
        if (receipt.Status != "OBSERVED" || receipt.Snapshot is null)
            throw new MachineException(4, "EXPORT_REFUSED", $"Export refused: {receipt.Status}");

        try
        {
            var preview = WorkbenchMetadataExport.Prepare(receipt.Snapshot, format);
            string? outputPath = null;
            if (!string.IsNullOrWhiteSpace(output))
            {
                outputPath = Path.GetFullPath(output);
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(outputPath, preview.Content, System.Text.Encoding.UTF8);
                preview = preview with { WasSaved = true };
            }

            return new
            {
                schema = preview.Schema,
                format = preview.Format,
                content = preview.Content,
                byte_count = preview.ContentBytes,
                was_saved = preview.WasSaved,
                requires_user_selected_destination = preview.RequiresUserSelectedDestination,
                output_path = outputPath,
                warning = preview.DataWarning
            };
        }
        catch (ArgumentException ex)
        {
            throw new MachineException(2, "INVALID_PARAMS", ex.Message);
        }
    }

    private static async Task<object> SessionStatusAsync(string[] args)
    {
        var sessionId = RequiredOption(args, "--session");
        var status = RequiredOption(args, "--status");
        var authority = ResolveAuthority(Option(args, "--port"));
        var session = await authority.InspectAsync(sessionId);
        if (session is null)
            throw new MachineException(2, "INVALID_PARAMS", "SESSION_NOT_FOUND");
        var outcome = await authority.SetStatusAsync(
            "cli-" + Guid.NewGuid().ToString("N"), session.Id, session.Version, status);
        if (!outcome.Applied)
            throw new MachineException(3, "OPERATION_REFUSED", outcome.Status);
        return new
        {
            schema = SharedSessionLedger.Schema,
            session_id = sessionId,
            status = outcome.Session?.SessionState,
            version = outcome.Session?.Version
        };
    }

    private static async Task<object> SessionCloseAsync(string[] args)
    {
        var sessionId = RequiredOption(args, "--session");
        var authority = ResolveAuthority(Option(args, "--port"));
        var session = await authority.InspectAsync(sessionId);
        if (session is null)
            throw new MachineException(2, "INVALID_PARAMS", "SESSION_NOT_FOUND");
        using var drafts = await LoadDraftsAsync(authority);
        drafts.Forget(sessionId);
        await SaveDraftsAsync(drafts);
        var outcome = await authority.CloseAsync(
            "cli-" + Guid.NewGuid().ToString("N"), session.Id, session.Version);
        if (!outcome.Applied)
            throw new MachineException(3, "OPERATION_REFUSED", outcome.Status);
        return new
        {
            schema = SharedSessionLedger.Schema,
            session_id = sessionId,
            closed = true,
            is_archived = outcome.Session?.IsArchived ?? true,
            version = outcome.Session?.Version
        };
    }

    private static async Task<object> SessionRestoreAsync(string[] args)
    {
        var sessionId = RequiredOption(args, "--session");
        var authority = ResolveAuthority(Option(args, "--port"));
        var session = await authority.InspectAsync(sessionId);
        if (session is null)
            throw new MachineException(2, "INVALID_PARAMS", "SESSION_NOT_FOUND");
        var outcome = await authority.RestoreAsync(
            "cli-" + Guid.NewGuid().ToString("N"), session.Id, session.Version);
        if (!outcome.Applied)
            throw new MachineException(3, "OPERATION_REFUSED", outcome.Status);
        return new
        {
            schema = SharedSessionLedger.Schema,
            session_id = sessionId,
            restored = true,
            is_archived = outcome.Session?.IsArchived ?? false,
            version = outcome.Session?.Version
        };
    }

    private static async Task<object> RpcSessionStatusSetAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var sessionId = RequiredPropertyEither(p, "sessionId", "session_id");
        var status = RequiredProperty(p, "status");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;
        var authority = ResolveAuthority(portStr);
        var session = await authority.InspectAsync(sessionId);
        if (session is null)
            throw new MachineException(2, "INVALID_PARAMS", "SESSION_NOT_FOUND");
        var outcome = await authority.SetStatusAsync(
            "rpc-" + Guid.NewGuid().ToString("N"), session.Id, session.Version, status);
        if (!outcome.Applied)
            throw new MachineException(3, "OPERATION_REFUSED", outcome.Status);
        return new
        {
            schema = SharedSessionLedger.Schema,
            session_id = sessionId,
            status = outcome.Session?.SessionState,
            version = outcome.Session?.Version
        };
    }

    private static async Task<object> RpcSessionCloseAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var sessionId = RequiredPropertyEither(p, "sessionId", "session_id");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;
        var authority = ResolveAuthority(portStr);
        var session = await authority.InspectAsync(sessionId);
        if (session is null)
            throw new MachineException(2, "INVALID_PARAMS", "SESSION_NOT_FOUND");
        using var drafts = await LoadDraftsAsync(authority);
        drafts.Forget(sessionId);
        await SaveDraftsAsync(drafts);
        var outcome = await authority.CloseAsync(
            "rpc-" + Guid.NewGuid().ToString("N"), session.Id, session.Version);
        if (!outcome.Applied)
            throw new MachineException(3, "OPERATION_REFUSED", outcome.Status);
        return new
        {
            schema = SharedSessionLedger.Schema,
            session_id = sessionId,
            closed = true,
            is_archived = outcome.Session?.IsArchived ?? true,
            version = outcome.Session?.Version
        };
    }

    private static async Task<object> RpcSessionRestoreAsync(JsonElement? parameters)
    {
        var p = RequireObject(parameters);
        var sessionId = RequiredPropertyEither(p, "sessionId", "session_id");
        string? portStr = p.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : null;
        var authority = ResolveAuthority(portStr);
        var session = await authority.InspectAsync(sessionId);
        if (session is null)
            throw new MachineException(2, "INVALID_PARAMS", "SESSION_NOT_FOUND");
        var outcome = await authority.RestoreAsync(
            "rpc-" + Guid.NewGuid().ToString("N"), session.Id, session.Version);
        if (!outcome.Applied)
            throw new MachineException(3, "OPERATION_REFUSED", outcome.Status);
        return new
        {
            schema = SharedSessionLedger.Schema,
            session_id = sessionId,
            restored = true,
            is_archived = outcome.Session?.IsArchived ?? false,
            version = outcome.Session?.Version
        };
    }

    private static string RequiredPropertyEither(JsonElement value, string name1, string name2)
    {
        if (value.TryGetProperty(name1, out var p1) && p1.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p1.GetString()))
            return p1.GetString()!;
        if (value.TryGetProperty(name2, out var p2) && p2.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p2.GetString()))
            return p2.GetString()!;
        throw new MachineException(2, "INVALID_PARAMS", $"Missing string params.{name1} or params.{name2}.");
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
                continue;
            if (i + 1 >= args.Length)
                throw new MachineException(2, "USAGE", $"Option {name} requires a value.");
            return args[i + 1];
        }
        return null;
    }

    private static IReadOnlyList<string> Options(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
                continue;
            if (i + 1 >= args.Length)
                throw new MachineException(2, "USAGE", $"Option {name} requires a value.");
            values.Add(args[++i]);
        }
        return values;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(x => string.Equals(x, name, StringComparison.Ordinal));

    private static string PlatformName() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";

    private static int WriteSuccess(object value)
    {
        WriteJson(new { ok = true, value });
        return 0;
    }

    private static int WriteFailure(int exitCode, string code, string message, object? detail = null)
    {
        WriteJson(new
        {
            ok = false,
            error = new { code, message, detail }
        });
        return exitCode;
    }

    private static void WriteJson(object value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, CliJsonOptions));

    private static async Task WriteRpcLineAsync(object value)
    {
        var line = JsonSerializer.Serialize(value, LineJsonOptions);
        await StdoutGate.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(line);
            await Console.Out.FlushAsync();
        }
        finally
        {
            StdoutGate.Release();
        }
    }

    private static string SafeMessage(Exception ex)
    {
        var message = ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        message = message.Replace("Bearer ", "Bearer <redacted> ", StringComparison.OrdinalIgnoreCase);
        return message.Length <= 1000 ? message : message[..1000] + "…";
    }

    private sealed class MachineException : Exception
    {
        public MachineException(int exitCode, string code, string message) : base(message)
        {
            ExitCode = exitCode;
            Code = code;
        }

        public int ExitCode { get; }
        public string Code { get; }
    }
}
