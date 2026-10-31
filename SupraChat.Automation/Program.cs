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

        var models = await Core.ListModelsAsync(policy: policy);
        var auth = await Core.GetAuthStatusAsync();
        return new
        {
            schema = Schema,
            binding = "siwc-responses",
            auth_mode = auth.AuthMode,
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

        var auth = await RequireAuthAsync(args);

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

        var auth = await RequireAuthAsync(args);
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
        var auth = await RequireAuthAsync();
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

    private static async Task<ResolvedAuthCredential> RequireAuthAsync(string[]? args = null, bool requirePlanUsage = false)
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
            return await Core.GetUsableAuthAsync(requirePlanUsage: requirePlanUsage, policy: policy);
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
