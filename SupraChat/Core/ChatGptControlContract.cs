namespace SupraChat.Core;

/// <summary>
/// Explicitly read-only contract for inspecting an existing ChatGPT conversation
/// reference. It does not attach to, authenticate, submit to, or export a live
/// ChatGPT WebUI session. Browser adapters must earn those capabilities separately.
/// </summary>
public static class ChatGptControlContract
{
    public const string Schema = "suprachat-chatgpt-control/v1";
    public const string InspectionSchema = "suprachat-chatgpt-conversation-inspection/v1";

    public static object Describe() => new
    {
        schema = Schema,
        source = "suprachat-issue-19",
        surface = "chatgpt-product-web",
        browser_adapter = "NOT_CONNECTED",
        credential_authority = "UNCHANGED",
        notes = "This is a semantic contract, not proof of live ChatGPT WebUI automation. Supported SIWC/Responses/Codex calls are different capability planes.",
        operations = new[]
        {
            Operation("chatgpt/conversation/inspect", "IMPLEMENTED_READ_ONLY", "suprachat", "read"),
            Operation("chatgpt/conversation/bind", "NOT_IMPLEMENTED", "suprachat", "bind"),
            Operation("chatgpt/conversation/readiness", "NOT_IMPLEMENTED", "userscripts-bridge-adapter", "observe"),
            Operation("chatgpt/conversation/prompt/submit", "NOT_IMPLEMENTED", "userscripts-bridge-adapter", "submit"),
            Operation("chatgpt/conversation/turn/observe", "NOT_IMPLEMENTED", "userscripts-bridge-adapter", "observe"),
            Operation("chatgpt/conversation/handoff", "NOT_IMPLEMENTED", "userscripts-handoff-adapter", "prepare"),
            Operation("chatgpt/conversation/export", "NOT_IMPLEMENTED", "suprachat-session-export", "export"),
            Operation("chatgpt/terminal/transfer/prepare", "NOT_IMPLEMENTED", "suprachat-workbench", "preview"),
            Operation("chatgpt/terminal/execute", "NOT_IMPLEMENTED", "separate-terminal-executor", "execute")
        },
        invariants = new[]
        {
            "An existing ChatGPT product conversation is not a Responses API thread.",
            "No raw browser cookie, ChatGPT private endpoint, or renewable token enters this contract.",
            "Observation does not constitute authorization or accepted completion.",
            "Preparing terminal text does not authorize shell execution.",
            "Human, accessibility, CLI, and agent surfaces must share operation identity.",
            "Missing live adapter or uncertain turn completion must fail closed."
        }
    };

    public static ChatGptConversationInspection Inspect(string? conversationUrl)
    {
        if (string.IsNullOrWhiteSpace(conversationUrl) ||
            !Uri.TryCreate(conversationUrl, UriKind.Absolute, out var uri))
            return Invalid("INVALID_URI");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return Invalid("INVALID_ORIGIN_OR_METADATA");

        // Deliberately accept only observed, unambiguous route shapes. A project
        // or GPT path requires a separately qualified adapter if it differs.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Contains('%') || path.Contains('\\'))
            return Invalid("UNSUPPORTED_ROUTE");

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isPlain = parts.Length == 2 && parts[0] == "c";
        var isGpt = parts.Length == 4 && parts[0] == "g" &&
                    !string.IsNullOrWhiteSpace(parts[1]) && parts[2] == "c";
        if ((!isPlain && !isGpt) ||
            !Guid.TryParseExact(parts[^1], "D", out var id))
            return Invalid("UNSUPPORTED_ROUTE");

        // Canonicalize only public-looking conversation identity metadata; this
        // routine never loads page content or checks authentication.
        var canonicalPath = isPlain
            ? $"/c/{id:D}"
            : $"/g/{parts[1]}/c/{id:D}";
        return new ChatGptConversationInspection(
            InspectionSchema,
            true,
            id.ToString("D"),
            "https://chatgpt.com" + canonicalPath,
            "NOT_CONNECTED",
            "URL shape recognized only. No authenticated browser/session verification was performed.",
            "NONE");
    }

    private static object Operation(string id, string status, string owner, string effect) =>
        new { id, status, owner, effect };

    private static ChatGptConversationInspection Invalid(string reason) =>
        new(InspectionSchema, false, null, null, "NOT_CONNECTED",
            "No browser/session access was attempted.", reason);
}

public sealed record ChatGptConversationInspection(
    string Schema,
    bool Recognized,
    string? ConversationId,
    string? CanonicalUrl,
    string AdapterState,
    string EvidenceBoundary,
    string ErrorCode);
