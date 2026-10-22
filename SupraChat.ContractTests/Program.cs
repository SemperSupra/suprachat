using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using SupraChat;
using SupraChat.Core;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// #19 W0: ChatGPT *product conversation* is not an SIWC/Responses thread.
var chatControl = ChatGptControlContract.Describe();
var chatControlJson = JsonSerializer.Serialize(chatControl);
Require(chatControlJson.Contains("chatgpt/conversation/prompt/submit", StringComparison.Ordinal),
    "ChatGPT control operation identity missing");
Require(chatControlJson.Contains("NOT_IMPLEMENTED", StringComparison.Ordinal),
    "Unqualified ChatGPT browser control must not be advertised as ready");
Require(chatControlJson.Contains("NOT_CONNECTED", StringComparison.Ordinal),
    "Missing real browser binding must be explicit");
var chatConversation = "123e4567-e89b-42d3-a456-426614174000";
var chatRef = ChatGptControlContract.Inspect("https://chatgpt.com/c/" + chatConversation);
Require(chatRef.Recognized && chatRef.ConversationId == chatConversation &&
    chatRef.AdapterState == "NOT_CONNECTED", "Conversation URL inspection state drifted");
var gptRef = ChatGptControlContract.Inspect("https://chatgpt.com/g/g-research/c/" + chatConversation);
Require(gptRef.Recognized && gptRef.CanonicalUrl!.Contains("/g/g-research/c/", StringComparison.Ordinal),
    "GPT-prefixed conversation binding was discarded");
foreach (var invalid in new[]
{
    "", "https://evil.example/c/" + chatConversation,
    "http://chatgpt.com/c/" + chatConversation,
    "https://chatgpt.com.evil.example/c/" + chatConversation,
    "https://chatgpt.com/c/" + chatConversation + "?access_token=secret",
    "https://chatgpt.com/c/" + chatConversation + "#private",
    "https://chatgpt.com/@other/c/" + chatConversation,
    "https://chatgpt.com/c/not-a-uuid",
    "https://chatgpt.com/",
})
{
    var rejected = ChatGptControlContract.Inspect(invalid);
    Require(!rejected.Recognized && rejected.ConversationId is null &&
        rejected.CanonicalUrl is null && rejected.AdapterState == "NOT_CONNECTED",
        "Unqualified/malformed conversation URL was accepted: " + invalid);
}


// #19 W0: inspect real workbench session semantics; terminal/chat mutation
// must remain impossible through this model.
var workbench = new SessionWorkbench();
var wbConversation = "https://chatgpt.com/c/123e4567-e89b-42d3-a456-426614174000";
var firstSession = workbench.Open("Research", wbConversation, "C:\dev\shared");
var secondSession = workbench.Open("Build", wbConversation, "C:/dev/shared");
var wbSnapshot = workbench.Snapshot();
Require(wbSnapshot.Schema == SessionWorkbench.Schema &&
        wbSnapshot.Sessions.Count == 2 &&
        wbSnapshot.ActiveSessionId == secondSession.Id &&
        wbSnapshot.Collisions.Any(c => c.Type == "DUPLICATE_CONVERSATION") &&
        wbSnapshot.Collisions.Any(c => c.Type == "SHARED_WORKSPACE") &&
        !wbSnapshot.ChatSubmissionPermitted &&
        !wbSnapshot.TerminalExecutionPermitted,
        "Multi-session collision/read-only contract violated");
Require(firstSession.TerminalState == "NOT_CONNECTED" &&
        !firstSession.TerminalExecutionPermitted && !firstSession.ChatSubmissionPermitted,
        "Session must not expose implicit terminal or chat send authority");
Require(workbench.Select(firstSession.Id) &&
        workbench.Move(firstSession.Id, 1) &&
        workbench.Snapshot().ActiveSessionId == firstSession.Id &&
        workbench.Snapshot().Sessions[1].Id == firstSession.Id,
        "Session selection/order invariant failed");
var transferPreview = workbench.PreviewTransfer(firstSession.Id, "chat-to-terminal", "echo safe preview");
Require(transferPreview.RequiresExplicitCommit && !transferPreview.WasApplied &&
        !transferPreview.CommandExecutionPermitted &&
        transferPreview.DestinationMode == "INSERT_ONLY_NOT_EXECUTE",
        "Workbench must never execute previewed text");
Require(!JsonSerializer.Serialize(workbench.Snapshot()).Contains("echo safe preview", StringComparison.Ordinal),
    "Preview text must not persist in session metadata");
var inversePreview = workbench.PreviewTransfer(firstSession.Id, "terminal-to-chat", "draft only");
Require(inversePreview.DestinationMode == "DRAFT_ONLY_NOT_SUBMIT" &&
        !inversePreview.WasApplied, "Terminal-to-chat preview must not submit");
Require(workbench.Close(secondSession.Id) && !workbench.Close(secondSession.Id) &&
        workbench.Snapshot().Collisions.Count == 0,
        "Closing sessions must remove stale duplicate/writable state");
var malformedWorkbenchUrlRejected = false;
try { workbench.Open("Bad", "https://evil.invalid/c/123e4567-e89b-42d3-a456-426614174000"); }
catch (ArgumentException) { malformedWorkbenchUrlRejected = true; }
Require(malformedWorkbenchUrlRejected, "Workbench accepted an arbitrary provider URL");
for (var i = workbench.Snapshot().Sessions.Count; i < SessionWorkbench.MaximumSessions; i++)
    workbench.Open($"Scratch {i}");
var tooManySessionsRejected = false;
try { workbench.Open("One too many"); } catch (InvalidOperationException) { tooManySessionsRejected = true; }
Require(tooManySessionsRejected && workbench.Snapshot().Sessions.Count == SessionWorkbench.MaximumSessions,
    "Workbench session bound is not enforced");
Require(JsonSerializer.Serialize(SessionWorkbench.DescribeContract()).Contains("transfer_commit_supported", StringComparison.Ordinal),
    "W0 workbench contract marker missing");

var attempt = SiwcProtocol.CreateAuthorization(
    "urn:uuid:11111111-2222-3333-4444-555555555555",
    "SupraChat");

Require(attempt.AuthorizationUri.Host == "auth.openai.com", "wrong auth host");
Require(attempt.AuthorizationUri.Query.Contains("client_id=dynamic_agent_client"), "dynamic client missing");
Require(attempt.AuthorizationUri.Query.Contains("chatgpt.tokens.use.direct"), "plan scope missing");
Require(attempt.AuthorizationUri.Query.Contains("ext_agent_host_id="), "host id missing");
Require(attempt.AuthorizationUri.Query.Contains("code_challenge_method=S256"), "PKCE S256 missing");
Require(attempt.AuthorizationUri.Query.Contains("agent_name_hint=SupraChat"), "registration name missing");

var returning = SiwcProtocol.CreateAuthorization(
    "urn:uuid:11111111-2222-3333-4444-555555555555",
    "SupraChat",
    "oaiapp_existing",
    idTokenHint: "header.payload.sig",
    loginHint: "user@example.com",
    promptConsent: true);
Require(!returning.AuthorizationUri.Query.Contains("agent_name_hint="), "returning sign-in must omit agent_name_hint");
Require(returning.AuthorizationUri.Query.Contains("id_token_hint="), "returning id token hint missing");
Require(returning.AuthorizationUri.Query.Contains("login_hint="), "returning login hint missing");
Require(returning.AuthorizationUri.Query.Contains("prompt=consent"), "re-consent prompt missing");

var callback = new Uri("http://127.0.0.1:54321/auth/callback?code=abc&state=xyz");
var exactRedirect = SiwcProtocol.CallbackRedirectUri(callback);
Require(exactRedirect.AbsoluteUri == "http://127.0.0.1:54321/auth/callback", "callback redirect must preserve allocated port");
var tokenForm = SiwcProtocol.BuildTokenForm(attempt, "oaiapp_issued", "code", exactRedirect);
Require(tokenForm["redirect_uri"] == exactRedirect.AbsoluteUri, "token exchange redirect must exactly match callback listener");
var refreshForm = SiwcProtocol.BuildRefreshForm("oaiapp_issued", "refresh");
Require(refreshForm["grant_type"] == "refresh_token", "refresh grant missing");
Require(!refreshForm.ContainsKey("scope"), "refresh must retain existing grant without resending scope");
var revokeForm = SiwcProtocol.BuildRevocationForm("oaiapp_issued", "refresh");
Require(revokeForm["token_type_hint"] == "refresh_token", "revocation hint missing");
Require(revokeForm["client_id"] == "oaiapp_issued", "revocation client missing");

using (var request = JsonDocument.Parse(SiwcProtocol.BuildResponsesBody("test-model", "hello")))
{
    Require(request.RootElement.GetProperty("store").GetBoolean() == false, "store must be false");
    Require(request.RootElement.GetProperty("stream").GetBoolean(), "stream must be true");
    Require(!request.RootElement.TryGetProperty("tools", out _), "plain response should not add tools");
}

var png = SiwcProtocol.ToDataUrl("image/png", new byte[] { 1, 2, 3, 4 });
var file = SiwcProtocol.ToDataUrl("text/plain", System.Text.Encoding.UTF8.GetBytes("hello"));
using (var multimodal = JsonDocument.Parse(SiwcProtocol.BuildResponsesBody(
    "test-model",
    "inspect",
    new[]
    {
        new ResponseAttachment("image", "sample.png", png),
        new ResponseAttachment("file", "sample.txt", file)
    },
    enableWebSearch: true)))
{
    var root = multimodal.RootElement;
    Require(root.GetProperty("store").GetBoolean() == false, "multimodal store must be false");
    Require(root.GetProperty("stream").GetBoolean(), "multimodal stream must be true");
    var tools = root.GetProperty("tools");
    Require(tools.GetArrayLength() == 1 && tools[0].GetProperty("type").GetString() == "web_search",
        "web search tool missing");

    var parts = root.GetProperty("input")[0].GetProperty("content");
    Require(parts.GetArrayLength() == 3, "text + image + file content expected");
    Require(parts[1].GetProperty("type").GetString() == "input_image", "image input missing");
    Require(parts[2].GetProperty("type").GetString() == "input_file", "file input missing");
    Require(parts[2].GetProperty("file_data").GetString()!.StartsWith("data:text/plain;base64,"),
        "file data URL missing");
}

using (var normalizedRaw = JsonDocument.Parse(ResponsesClient.NormalizeRawResponsesBody(
    """{"input":[{"role":"user","content":[{"type":"input_text","text":"hello"}]}],"tools":[{"type":"web_search"}],"temperature":0.2}""",
    "gpt-example")))
{
    var root = normalizedRaw.RootElement;
    Require(root.GetProperty("model").GetString() == "gpt-example", "raw Responses default model injection failed");
    Require(root.GetProperty("store").GetBoolean() == false, "raw Responses store must be forced false");
    Require(root.GetProperty("stream").GetBoolean(), "raw Responses stream must be forced true");
    Require(root.GetProperty("tools")[0].GetProperty("type").GetString() == "web_search",
        "raw Responses supported tool field was filtered");
    Require(root.GetProperty("temperature").GetDouble() == 0.2,
        "raw Responses probe field was silently removed instead of reaching upstream validation");
}

using (var wsCreate = JsonDocument.Parse(ResponsesWebSocketClient.NormalizeClientEvent(
    """{"type":"response.create","stream_id":"planner","store":true,"stream":true,"background":true,"input":[{"role":"user","content":"hello"}]}""",
    "gpt-example")))
{
    var root = wsCreate.RootElement;
    Require(root.GetProperty("type").GetString() == "response.create", "WebSocket response.create type changed");
    Require(root.GetProperty("model").GetString() == "gpt-example", "WebSocket default model injection failed");
    Require(root.GetProperty("store").GetBoolean() == false, "WebSocket ChatGPT-plan store must be forced false");
    Require(root.GetProperty("stream_id").GetString() == "planner", "WebSocket stream_id must survive normalization");
    Require(!root.TryGetProperty("stream", out _), "WebSocket event must omit HTTP-only stream field");
    Require(!root.TryGetProperty("background", out _), "WebSocket event must omit background field");
}

using (var wsSteer = JsonDocument.Parse(ResponsesWebSocketClient.NormalizeClientEvent(
    """{"type":"response.steer","previous_response_id":"resp_1","input":"narrow the scope"}""",
    "gpt-example")))
{
    var root = wsSteer.RootElement;
    Require(root.GetProperty("type").GetString() == "response.steer", "WebSocket steer type changed");
    Require(root.GetProperty("previous_response_id").GetString() == "resp_1", "WebSocket steer lineage changed");
    Require(!root.TryGetProperty("model", out _), "Non-create WebSocket events must remain pass-through");
}

var modelJson = """{"models":[{"slug":"gpt-example","display_name":"GPT Example","visibility":"list"},{"slug":"hidden","display_name":"Hidden","visibility":"hidden"}]}""";
var models = ResponsesClient.ParseModels(modelJson);
Require(models.Count == 1 && models[0].Slug == "gpt-example" && models[0].DisplayName == "GPT Example", "SIWC model catalog parsing failed");

Require(CodexAppServer.Arguments.Contains("model_provider=\"openai_chatgpt_plan\""), "Codex provider missing");
Require(CodexAppServer.Arguments.Any(x => x.Contains("requires_openai_auth=false")), "Codex auth mode missing");
Require(CodexAppServer.Arguments.All(x => !x.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)), "token leaked into arguments");

var codexInit = CodexAppServerClient.BuildInitializeParams(experimentalApi: true);
Require(codexInit.GetProperty("clientInfo").GetProperty("name").GetString() == "suprachat", "Codex initialize client identity missing");
Require(codexInit.GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean(), "Codex experimental protocol capability must be enabled");

var codexThread = CodexAppServerClient.BuildThreadStartParams("gpt-example", Path.GetFullPath("."));
Require(codexThread.GetProperty("modelProvider").GetString() == "openai_chatgpt_plan", "Codex ChatGPT-plan provider missing");
Require(codexThread.GetProperty("approvalPolicy").GetString() == "never", "qualification interview approval policy changed");
Require(codexThread.GetProperty("sandbox").GetString() == "read-only", "qualification interview sandbox changed");
Require(codexThread.GetProperty("ephemeral").GetBoolean(), "qualification interview thread must be ephemeral");

var codexTurn = CodexAppServerClient.BuildTurnStartParams("thread-test", "hello");
Require(codexTurn.GetProperty("threadId").GetString() == "thread-test", "Codex turn thread id missing");
Require(codexTurn.GetProperty("input")[0].GetProperty("type").GetString() == "text", "Codex text input missing");

var surfacePath = Path.Combine(
    "oracles", "codex-app-server-surface-20261001.json");
Require(File.Exists(surfacePath), "pinned Codex surface inventory missing");
using (var surfaceDoc = JsonDocument.Parse(File.ReadAllText(surfacePath)))
{
    var counts = surfaceDoc.RootElement.GetProperty("counts");
    Require(counts.GetProperty("client_requests").GetInt32() == 172, "Codex client-RPC inventory drifted");
    Require(counts.GetProperty("server_requests").GetInt32() == 9, "Codex server-request inventory drifted");
    Require(counts.GetProperty("server_notifications").GetInt32() == 85, "Codex notification inventory drifted");
    Require(counts.GetProperty("total").GetInt32() == 266, "Codex total protocol inventory drifted");
}

var fakeCredential = new SiwcCredential(
    "oaiapp_test",
    "urn:uuid:11111111-2222-3333-4444-555555555555",
    "subject-secret-value",
    "user@example.com",
    "ACCESS_TOKEN_MUST_NOT_APPEAR",
    "REFRESH_TOKEN_MUST_NOT_APPEAR",
    "ID_TOKEN_MUST_NOT_APPEAR",
    "Bearer",
    3600,
    new[] { "openid", SiwcProtocol.RequiredPlanScope },
    DateTimeOffset.UtcNow);

Require(fakeCredential.HasPlanUsage, "plan-usage scope should be recognized");
var identityOnly = fakeCredential with { Scopes = new[] { "openid", "profile" } };
Require(!identityOnly.HasPlanUsage, "identity-only session must remain distinct from plan usage");

var portableCredential = fakeCredential with
{
    Generation = 7,
    EarliestRefreshAtRaw = "1790033000"
};
var bundle = PortableCredentialBundle.Protect(
    portableCredential,
    "correct horse battery staple",
    exportedAt: DateTimeOffset.Parse("2026-10-03T10:00:00Z"));
var bundleJson = JsonSerializer.Serialize(bundle);
Require(bundle.Schema == PortableCredentialBundle.Schema, "portable credential bundle schema mismatch");
Require(bundle.Mode == PortableCredentialBundle.CopyMode, "portable credential bundle mode mismatch");
Require(!bundleJson.Contains("ACCESS_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal),
    "access token leaked into portable credential envelope");
Require(!bundleJson.Contains("REFRESH_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal),
    "refresh token leaked into portable credential envelope");
Require(!bundleJson.Contains("ID_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal),
    "ID token leaked into portable credential envelope");
Require(!bundleJson.Contains("subject-secret-value", StringComparison.Ordinal),
    "raw subject leaked into portable credential envelope");
Require(!bundleJson.Contains("subject_fingerprint", StringComparison.Ordinal),
    "portable credential envelope should not expose a stable account fingerprint");

var restoredCredential = PortableCredentialBundle.Unprotect(
    bundle,
    "correct horse battery staple");
Require(restoredCredential.AccessToken == portableCredential.AccessToken,
    "portable credential access-token round trip failed");
Require(restoredCredential.RefreshToken == portableCredential.RefreshToken,
    "portable credential refresh-token round trip failed");
Require(restoredCredential.IdToken == portableCredential.IdToken,
    "portable credential ID-token round trip failed");
Require(restoredCredential.Generation == 7,
    "portable credential generation round trip failed");
Require(restoredCredential.EarliestRefreshAtRaw == "1790033000",
    "opaque earliest_refresh_at metadata was not preserved");

var wrongPassphraseRejected = false;
try
{
    _ = PortableCredentialBundle.Unprotect(bundle, "this passphrase is wrong");
}
catch (System.Security.Cryptography.CryptographicException)
{
    wrongPassphraseRejected = true;
}
Require(wrongPassphraseRejected, "portable credential bundle accepted a wrong passphrase");

var newerCredential = portableCredential with { Generation = 8 };
Require(CredentialStore.WouldRegressGeneration(newerCredential, portableCredential),
    "stale credential generation was not detected");
Require(!CredentialStore.WouldRegressGeneration(portableCredential, newerCredential),
    "newer credential generation was incorrectly rejected");
var conflictingCredential = portableCredential with { RefreshToken = "DIFFERENT_ROTATING_REFRESH_TOKEN" };
Require(CredentialStore.WouldConflictGeneration(portableCredential, conflictingCredential),
    "same-generation rotating refresh-token conflict was not detected");
Require(!CredentialStore.WouldConflictGeneration(portableCredential, portableCredential),
    "identical same-generation credential was incorrectly treated as conflicting");

var authDescriptor = SupraChatCore.DescribeCredential(portableCredential);
var authDescriptorJson = JsonSerializer.Serialize(authDescriptor);
Require(authDescriptor.CredentialGeneration == 7, "core auth descriptor lost credential generation");
Require(authDescriptor.EarliestRefreshMetadataPresent, "core auth descriptor lost earliest-refresh presence");
Require(!authDescriptorJson.Contains("subject-secret-value", StringComparison.Ordinal),
    "core auth descriptor leaked raw subject");
Require(!authDescriptorJson.Contains("ACCESS_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal),
    "core auth descriptor leaked access token");
Require(!authDescriptorJson.Contains("REFRESH_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal),
    "core auth descriptor leaked refresh token");

var receipt = QualificationReceipts.BuildDirect(
    "gpt-example",
    fakeCredential,
    completed: true,
    TimeSpan.FromMilliseconds(1234),
    outputCharacters: 42);
var receiptJson = JsonSerializer.Serialize(receipt);

Require(receipt.Schema == QualificationReceipts.Schema, "receipt schema mismatch");
Require(receipt.Binding == "siwc-direct-responses", "receipt binding mismatch");
Require(receipt.Completed && !receipt.Store && receipt.Stream, "receipt completion/transport invariants wrong");
Require(!receipt.ContainsPrompt && !receipt.ContainsOutput && !receipt.ContainsTokenMaterial, "receipt privacy flags wrong");
Require(receipt.GrantedScopes.Contains(SiwcProtocol.RequiredPlanScope), "receipt must retain granted-scope metadata");
Require(!receiptJson.Contains("ACCESS_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal), "access token leaked into receipt");
Require(!receiptJson.Contains("REFRESH_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal), "refresh token leaked into receipt");
Require(!receiptJson.Contains("ID_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal), "ID token leaked into receipt");
Require(!receiptJson.Contains("subject-secret-value", StringComparison.Ordinal), "raw account subject leaked into receipt");
Require(!receiptJson.Contains("user@example.com", StringComparison.Ordinal), "email leaked into receipt");

var codexReceipt = QualificationReceipts.BuildCodex(
    "gpt-example",
    fakeCredential,
    completed: true,
    TimeSpan.FromMilliseconds(1500),
    outputCharacters: 55);
var codexReceiptJson = JsonSerializer.Serialize(codexReceipt);
Require(codexReceipt.Binding == "siwc-codex-app-server", "Codex receipt binding mismatch");
Require(codexReceipt.Runtime.Contains("Codex app-server", StringComparison.Ordinal), "Codex receipt runtime missing");
Require(!codexReceiptJson.Contains("ACCESS_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal), "access token leaked into Codex receipt");
Require(!codexReceiptJson.Contains("REFRESH_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal), "refresh token leaked into Codex receipt");
Require(!codexReceiptJson.Contains("ID_TOKEN_MUST_NOT_APPEAR", StringComparison.Ordinal), "ID token leaked into Codex receipt");
Require(!codexReceiptJson.Contains("subject-secret-value", StringComparison.Ordinal), "raw account subject leaked into Codex receipt");
Require(!codexReceiptJson.Contains("user@example.com", StringComparison.Ordinal), "email leaked into Codex receipt");

var runtimeRoot = Path.Combine(Path.GetTempPath(), "suprachat-codex-resolution-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(runtimeRoot, "runtime", "codex"));
var bundledName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
var bundledPath = Path.Combine(runtimeRoot, "runtime", "codex", bundledName);
File.WriteAllText(bundledPath, "fixture");
Require(CodexAppServer.ResolveExecutable(runtimeRoot) == bundledPath,
    "packaged Codex runtime must take precedence over PATH fallback");
Directory.Delete(runtimeRoot, recursive: true);

Require(WindowsCompanionHotkey.ShortcutDescription == "Alt+Space",
    "Windows companion shortcut drifted from first-party parity target");

Require(WindowsNotificationService.Adapter == "shell-notification-area",
    "Windows notification adapter identity drifted");
Require(DesktopNotificationService.Adapter is not "unsupported",
    "desktop notification adapter missing for supported platform");
Require(DesktopNotificationService.AppleScriptString("a\"b") == "\"a\\\"b\"",
    "macOS notification string escaping drifted");

// NativeControlHost can create layered child HWNDs only with modern Windows compatibility.
var desktopProject = XDocument.Load(Path.Combine("SupraChat", "SupraChat.csproj"));
Require(desktopProject.Descendants("ApplicationManifest").Single().Value == "app.manifest",
    "desktop executable must embed its Windows compatibility manifest");
var desktopManifest = XDocument.Load(Path.Combine("SupraChat", "app.manifest"));
XNamespace compatibilityNamespace = "urn:schemas-microsoft-com:compatibility.v1";
Require(desktopManifest.Descendants(compatibilityNamespace + "supportedOS")
        .Any(os => (string?)os.Attribute("Id") == "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"),
    "native control hosting requires Windows 10+ compatibility declaration");
XNamespace securityNamespace = "urn:schemas-microsoft-com:asm.v3";
var executionLevel = desktopManifest.Descendants(securityNamespace + "requestedExecutionLevel").Single();
Require((string?)executionLevel.Attribute("level") == "asInvoker" &&
        (string?)executionLevel.Attribute("uiAccess") == "false",
    "desktop manifest must preserve per-user execution without elevated UI access");

var parityPath = Path.Combine("oracles", "audience-parity-20261001.json");
Require(File.Exists(parityPath), "audience/accessibility parity manifest missing");
using (var parityDoc = JsonDocument.Parse(File.ReadAllText(parityPath)))
{
    var root = parityDoc.RootElement;
    Require(root.GetProperty("schema").GetString() == "suprachat-audience-parity/v1",
        "audience parity schema drifted");
    var features = root.GetProperty("features");
    Require(features.GetArrayLength() >= 15, "audience parity ledger unexpectedly sparse");

    var allowedParityStates = new HashSet<string>(new[]
    {
        "implemented",
        "implemented_with_fallback",
        "qualify",
        "human_boundary",
        "gap",
        "not_applicable"
    }, StringComparer.Ordinal);

    foreach (var feature in features.EnumerateArray())
    {
        Require(feature.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()),
            "parity feature id missing");
        Require(feature.TryGetProperty("access", out var access) && access.ValueKind == JsonValueKind.Object,
            $"parity access map missing for {id.GetString()}");
        foreach (var audience in new[] { "human", "accessible_human", "automation", "agent" })
        {
            Require(access.TryGetProperty(audience, out var state) &&
                    !string.IsNullOrWhiteSpace(state.GetString()),
                $"parity audience state missing for {id.GetString()}: {audience}");
            Require(allowedParityStates.Contains(state.GetString()!),
                $"unknown parity audience state for {id.GetString()}: {audience}={state.GetString()}");
        }

        if (feature.TryGetProperty("consequential", out var consequential) && consequential.GetBoolean())
        {
            var humanState = access.GetProperty("human").GetString();
            var accessibleState = access.GetProperty("accessible_human").GetString();
            Require(!(humanState == "implemented" && accessibleState == "gap"),
                $"accessible-human regression for consequential feature {id.GetString()}");
            Require(!(humanState == "implemented" && accessibleState == "not_applicable"),
                $"accessible-human path cannot be not_applicable for consequential human feature {id.GetString()}");
        }

        Require(feature.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Object,
            $"parity platform map missing for {id.GetString()}");
        foreach (var platform in new[] { "windows", "macos", "linux" })
        {
            Require(platforms.TryGetProperty(platform, out var state) &&
                    !string.IsNullOrWhiteSpace(state.GetString()),
                $"parity platform state missing for {id.GetString()}: {platform}");
            Require(allowedParityStates.Contains(state.GetString()!),
                $"unknown parity platform state for {id.GetString()}: {platform}={state.GetString()}");
        }

        Require(feature.TryGetProperty("human_boundary", out var boundary) &&
                !string.IsNullOrWhiteSpace(boundary.GetString()),
            $"parity authorization boundary missing for {id.GetString()}");
    }
}

var attachmentFixture = AttachmentInputs.Create(
    "sample.txt",
    System.Text.Encoding.UTF8.GetBytes("accessible machine attachment"));
Require(attachmentFixture.Kind == "file", "shared attachment MIME classification drifted");
Require(attachmentFixture.Value.StartsWith("data:text/plain;base64,", StringComparison.Ordinal),
    "shared attachment data URL drifted");

var screenCaptureDescriptor = DesktopScreenCapture.Describe();
Require(screenCaptureDescriptor.Schema == DesktopScreenCapture.Schema,
    "screen capture descriptor schema drifted");
Require(screenCaptureDescriptor.RequiresExplicitInvocation,
    "screen capture must remain an explicit-invocation sensor");
Require(!string.IsNullOrWhiteSpace(screenCaptureDescriptor.PermissionBoundary),
    "screen capture permission boundary missing");

var accessibilityContract = AccessibilityContract.Describe();
Require(accessibilityContract.Schema == AccessibilityContract.Schema, "accessibility schema drifted");
Require(accessibilityContract.Human.KeyboardOnly, "keyboard-only accessibility invariant missing");
Require(accessibilityContract.Human.ScreenReaderSemantics, "screen-reader semantic invariant missing");
Require(accessibilityContract.Human.StableAutomationIds, "stable automation-id invariant missing");
Require(accessibilityContract.Human.BrowserAccessibilityFallback, "browser accessibility fallback invariant missing");
Require(accessibilityContract.Human.UserAdjustableInterfaceScale, "user-adjustable interface scale invariant missing");
Require(accessibilityContract.Human.InterfaceScaleMinimum <= 0.8 &&
        accessibilityContract.Human.InterfaceScaleMaximum >= 2.0,
    "accessible interface-scale range regressed");
Require(accessibilityContract.Human.ReducedMotionPreference, "reduced-motion preference invariant missing");
Require(!accessibilityContract.Automation.ScreenScrapingRequired, "automation accessibility regressed to screen scraping");
Require(!accessibilityContract.Agent.ScreenScrapingRequired, "agent accessibility regressed to screen scraping");

var accessibilityDocPath = Path.Combine("ACCESSIBILITY.md");
Require(File.Exists(accessibilityDocPath), "accessibility contract document missing");
var accessibilityDoc = File.ReadAllText(accessibilityDocPath);
Require(accessibilityDoc.Contains("Accessibility regressions are qualification failures", StringComparison.Ordinal),
    "accessibility qualification invariant missing");

var mainWindowXamlPath = Path.Combine("SupraChat", "MainWindow.axaml");
Require(File.Exists(mainWindowXamlPath), "MainWindow accessibility surface missing");
var mainWindowXaml = File.ReadAllText(mainWindowXamlPath);
Require(mainWindowXaml.Contains("<local:SecurePassphraseTextBox x:Name=\"CredentialPassphraseBox\"", StringComparison.Ordinal),
    "portable credential passphrase must use the hardened secure textbox");
Require(!mainWindowXaml.Contains("<TextBox x:Name=\"CredentialPassphraseBox\"", StringComparison.Ordinal),
    "portable credential passphrase regressed to the stock TextBox automation peer");

var securePassphrase = new SecurePassphraseTextBox();
Require(securePassphrase.PasswordChar == '●' && !securePassphrase.RevealPassword,
    "secure passphrase textbox must mask by default");
var securePassphrasePeer = ControlAutomationPeer.CreatePeerForElement(securePassphrase);
Require(securePassphrasePeer.GetAutomationControlType() == AutomationControlType.Edit,
    "secure passphrase textbox must remain an accessible edit control");
Require(securePassphrasePeer.GetProvider<IValueProvider>() is null,
    "secure passphrase textbox must not expose secret text through automation ValuePattern");
foreach (var marker in new[]
{
    "AutomationProperties.AutomationId=\"SupraChat.MainWindow\"",
    "AutomationProperties.AutomationId=\"RootTabs\"",
    "AutomationProperties.AutomationId=\"AgentLab.Prompt\"",
    "AutomationProperties.AutomationId=\"AgentLab.CaptureScreen\"",
    "AutomationProperties.AutomationId=\"Auth.Status\"",
    "AutomationProperties.AutomationId=\"Auth.PortableHeading\"",
    "AutomationProperties.AutomationId=\"Auth.BundlePassphrase\"",
    "AutomationProperties.AutomationId=\"Auth.ExportCredential\"",
    "AutomationProperties.AutomationId=\"Auth.ImportCredential\"",
    "AutomationProperties.AutomationId=\"Auth.PortableStatus\"",
    "AutomationProperties.AutomationId=\"Accessibility.Status\"",
    "AutomationProperties.AutomationId=\"Accessibility.ScaleDown\"",
    "AutomationProperties.AutomationId=\"Accessibility.ScaleReset\"",
    "AutomationProperties.AutomationId=\"Accessibility.ScaleUp\"",
    "AutomationProperties.AutomationId=\"Accessibility.ReducedMotion\"",
    "AutomationProperties.AutomationId=\"Accessibility.PreferencesStatus\"",
    "AutomationProperties.AutomationId=\"Machine.AudienceParityStatus\"",
    "AutomationProperties.AutomationId=\"Machine.ExportDiagnostics\"",
    "AutomationProperties.AutomationId=\"Machine.DiagnosticsStatus\"",
    "AutomationProperties.AutomationId=\"RuntimeProbes.Voices\"",
    "AutomationProperties.AutomationId=\"RuntimeProbes.RemoteStatus\"",
    "AutomationProperties.AutomationId=\"RuntimeProbes.Plugins\"",
    "AutomationProperties.AutomationId=\"RuntimeProbes.PermissionProfiles\"",
    "AutomationProperties.AutomationId=\"RuntimeProbes.Apps\"",
    "AutomationProperties.AutomationId=\"RuntimeProbes.SandboxReadiness\"",
    "AutomationProperties.AutomationId=\"RuntimeProbes.Output\"",
    "AutomationProperties.AutomationId=\"RemoteControl.Confirm\"",
    "AutomationProperties.AutomationId=\"RemoteControl.Enable\"",
    "AutomationProperties.AutomationId=\"RemoteControl.Disable\"",
    "AutomationProperties.AutomationId=\"RemoteControl.PairingStart\"",
    "AutomationProperties.AutomationId=\"RemoteControl.PairingStatus\"",
    "AutomationProperties.AutomationId=\"RemoteControl.Clients\"",
    "AutomationProperties.AutomationId=\"RemoteControl.Revoke\"",
    "AutomationProperties.AutomationId=\"Browser.Url\"",
    "AutomationProperties.AutomationId=\"Browser.Status\"",
    "AutomationProperties.AutomationId=\"Browser.StartInspect\"",
    "AutomationProperties.AutomationId=\"Browser.Stop\"",
    "AutomationProperties.AutomationId=\"Browser.SemanticSnapshot\"",
    "AutomationProperties.LiveSetting=\"Polite\"",
    "AutomationProperties.HeadingLevel=\"1\"",
    "AutomationProperties.HeadingLevel=\"2\"",
    "KeyDown=\"MainWindow_KeyDown\""
})
{
    Require(mainWindowXaml.Contains(marker, StringComparison.Ordinal),
        $"required accessibility marker missing: {marker}");
}

var normalizedPreference = AccessibilityPreferencesStore.Normalize(
    new AccessibilityPreferences("wrong", 4.7, true));
Require(normalizedPreference.Schema == AccessibilityPreferencesStore.Schema,
    "accessibility preference schema normalization failed");
Require(normalizedPreference.InterfaceScale == AccessibilityPreferencesStore.MaximumScale,
    "accessibility scale maximum clamp failed");
Require(normalizedPreference.ReducedMotion,
    "reduced-motion preference was lost during normalization");

var automationSourcePath = Path.Combine(
    "SupraChat.Automation", "Program.cs");
Require(File.Exists(automationSourcePath), "automation source missing");
var automationSource = File.ReadAllText(automationSourcePath);
foreach (var marker in new[]
{
    "\"voices\"",
    "\"remote-status\"",
    "\"plugins\"",
    "\"permission-profiles\"",
    "\"apps\"",
    "\"sandbox-readiness\"",
    "\"realtime/voices\"",
    "\"remote/status\"",
    "\"plugins/list\"",
    "\"permissions/profiles\"",
    "\"apps/list\"",
    "\"sandbox/readiness\"",
    "\"browser-status\"",
    "\"browser-snapshot\"",
    "\"browser/status\"",
    "\"browser/snapshot\"",
    "\"browser/start\"",
    "\"browser/navigate\"",
    "\"browser/read\"",
    "\"browser/click\"",
    "\"browser/fill\"",
    "\"browser/stop\"",
    "\"browser-click\"",
    "\"browser-fill\"",
    "\"diagnostics\"",
    "\"diagnostics-export\"",
    "\"diagnostics/read\"",
    "\"diagnostics/export\"",
    "\"auth-bundle-inspect\"",
    "\"auth-export\"",
    "\"auth-import\"",
    "\"auth/bundle/inspect\"",
    "\"auth/export\"",
    "\"auth/import\""
})
{
    Require(automationSource.Contains(marker, StringComparison.Ordinal),
        $"semantic machine runtime surface missing: {marker}");
}

var coreDocPath = Path.Combine("docs", "core-api-portable-credentials.md");
Require(File.Exists(coreDocPath), "core API / portable credential architecture document missing");
var coreDoc = File.ReadAllText(coreDocPath);
Require(coreDoc.Contains("SupraChat Core API", StringComparison.Ordinal),
    "core API architecture decision missing");
Require(coreDoc.Contains("remote credential broker / lease service", StringComparison.Ordinal),
    "deferred broker boundary missing");

var browserStatus = BrowserSession.Status();
Require(browserStatus.Schema == "suprachat-browser-runtime/v1", "browser runtime schema drifted");
Require(browserStatus.EphemeralByDefault, "clean-room browser must remain ephemeral by default");
Require(browserStatus.AccessibilitySnapshotSupported, "semantic browser snapshot invariant missing");
var browserSource = File.ReadAllText(Path.Combine("SupraChat", "Core", "BrowserSession.cs"));
Require(browserSource.Contains("ClickByRoleAsync", StringComparison.Ordinal),
    "semantic browser role/name click actuator missing");
Require(browserSource.Contains("FillByLabelAsync", StringComparison.Ordinal),
    "semantic browser labeled-input fill actuator missing");
Require(!browserSource.Contains("Mouse.ClickAsync", StringComparison.Ordinal),
    "semantic browser tranche regressed to coordinate-first clicking");

var browserProject = File.ReadAllText(Path.Combine("SupraChat", "SupraChat.csproj"));
Require(browserProject.Contains("Microsoft.Playwright\" Version=\"1.63.0\"", StringComparison.Ordinal),
    "Playwright browser runtime version drifted");

var observability = DogfoodObservability.Describe();
Require(observability.Schema == "suprachat-dogfood-observability/v2",
    "dogfood observability schema drifted");
Require(observability.TraceId.Length == 32 &&
        observability.TraceId.All(Uri.IsHexDigit) &&
        observability.TraceId.Any(c => c != '0'),
    "W3C-compatible trace id is malformed");
Require(!observability.ContainsPrompts &&
        !observability.ContainsOutputs &&
        !observability.ContainsTokenMaterial,
    "dogfood observability privacy boundary regressed");

var utcTimestamp = DogfoodObservability.UtcTimestamp();
Require(utcTimestamp.EndsWith("Z", StringComparison.Ordinal),
    "dogfood timestamp must be explicit UTC with Z designator");
Require(DateTimeOffset.TryParse(utcTimestamp, out var parsedUtc) &&
        parsedUtc.Offset == TimeSpan.Zero,
    "dogfood timestamp is not parseable UTC ISO-8601/RFC3339");

var correlated = DogfoodObservability.BeginOperation(
    "qualification-correlation",
    correlationId: "correlation-test");
Require(correlated.CorrelationId == "correlation-test",
    "explicit dogfood correlation id was not preserved");
Require(correlated.TraceId == observability.TraceId,
    "operation trace must inherit the process/install trace");
Require(correlated.SpanId.Length == 16 &&
        correlated.SpanId.All(Uri.IsHexDigit) &&
        correlated.SpanId.Any(c => c != '0'),
    "W3C-compatible span id is malformed");

var installerSource = File.ReadAllText(Path.Combine(
    "packaging", "windows", "install.ps1"));
foreach (var marker in new[]
{
    "timestamp_utc",
    "install_session_id",
    "trace_id",
    "span_id",
    "correlation_id",
    "app_sha256",
    "automation_sha256",
    "codex_sha256",
    "contains_tokens = $false",
    "SUPRACHAT_INSTALL_TRACE_ID"
})
{
    Require(installerSource.Contains(marker, StringComparison.Ordinal),
        $"Windows installer observability marker missing: {marker}");
}

var appXamlPath = Path.Combine("SupraChat", "App.axaml");
Require(File.ReadAllText(appXamlPath).Contains("RequestedThemeVariant=\"Default\"", StringComparison.Ordinal),
    "application must follow the platform theme/high-contrast preference");

Console.WriteLine("SupraChat contract checks PASS");
