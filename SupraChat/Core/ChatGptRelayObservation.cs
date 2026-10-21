using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// Observe bounded metadata from an independently authorized local loopback
/// userscript relay. This is NOT a live ChatGPT session attestation and never
/// grants authority to submit, export, bind, or execute a command.
/// </summary>
public static class ChatGptRelayObservation
{
    public const string Schema = "suprachat-chatgpt-relay-observation/v1";
    private const int MaxStateBytes = 64 * 1024;

    public static async Task<ChatGptRelayState> ObserveAsync(
        string? conversationUrl,
        int port,
        string? relayToken,
        CancellationToken cancellationToken = default)
    {
        var inspection = ChatGptControlContract.Inspect(conversationUrl);
        if (!inspection.Recognized)
            return Unavailable("INVALID_CONVERSATION_REFERENCE");
        if (port is < 1 or > 65535)
            return Unavailable("INVALID_RELAY_PORT");
        if (string.IsNullOrWhiteSpace(relayToken))
            return Unavailable("NO_LOCAL_RELAY_TOKEN");

        // Deliberately no DNS, HTTP proxy, redirects, cookie jar, or broad URL
        // argument. The authenticated metadata endpoint cannot be repointed
        // at a non-loopback service or provider-private endpoint.
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"http://127.0.0.1:{port}/state"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relayToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Unavailable("RELAY_AUTHENTICATION_FAILED");
            if (response.StatusCode != HttpStatusCode.OK)
                return Unavailable("RELAY_UNAVAILABLE");
            if (response.Content.Headers.ContentLength > MaxStateBytes)
                return Unavailable("RELAY_METADATA_LIMIT_EXCEEDED");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var bytesRead = await input.ReadAsync(chunk.AsMemory(), cancellationToken);
                if (bytesRead == 0) break;
                if (buffer.Length + bytesRead > MaxStateBytes)
                    return Unavailable("RELAY_METADATA_LIMIT_EXCEEDED");
                buffer.Write(chunk, 0, bytesRead);
            }
            return Interpret(inspection.CanonicalUrl!, Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (OperationCanceledException) { return Unavailable("RELAY_UNAVAILABLE"); }
        catch (HttpRequestException) { return Unavailable("RELAY_UNAVAILABLE"); }
        catch (IOException) { return Unavailable("RELAY_UNAVAILABLE"); }
        catch (JsonException) { return Unavailable("INVALID_RELAY_METADATA"); }
    }

    /// <summary>
    /// Pure, network-free projection for synthetic cross-platform contract tests.
    /// Never emits client IDs, request IDs, prompt text, body text or tokens.
    /// </summary>
    public static ChatGptRelayState Interpret(string? conversationUrl, string stateJson)
    {
        var inspection = ChatGptControlContract.Inspect(conversationUrl);
        if (!inspection.Recognized)
            return Unavailable("INVALID_CONVERSATION_REFERENCE");

        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("clients", out var clients) ||
                clients.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("queueDepth", out var queue) ||
                !queue.TryGetInt32(out var queueDepth) || queueDepth < 0 ||
                !root.TryGetProperty("quarantineCounts", out var quarantine) ||
                quarantine.ValueKind != JsonValueKind.Object ||
                !NonnegativeCount(quarantine, "clients", out var lockedClients) ||
                !NonnegativeCount(quarantine, "conversations", out var lockedConversations))
                return Unavailable("INVALID_RELAY_METADATA");

            var matching = 0;
            foreach (var client in clients.EnumerateArray())
            {
                if (client.ValueKind != JsonValueKind.Object ||
                    !client.TryGetProperty("conversationUrl", out var url) ||
                    url.ValueKind != JsonValueKind.String ||
                    !client.TryGetProperty("browserState", out var state) ||
                    state.ValueKind != JsonValueKind.String)
                    return Unavailable("INVALID_RELAY_METADATA");

                // Compare full canonical origin AND route, never just a GUID.
                if (string.Equals(url.GetString(), inspection.CanonicalUrl, StringComparison.Ordinal) &&
                    string.Equals(state.GetString(), "ready", StringComparison.Ordinal))
                    matching++;
            }
            return new ChatGptRelayState(
                Schema, true,
                matching > 0 ? "RELAY_REPORTS_READY_UNVERIFIED" : "NO_MATCHING_READY_CLIENT",
                inspection.CanonicalUrl, matching, queueDepth,
                lockedClients, lockedConversations, "NONE",
                "Authenticated local relay self-report only; neither browser identity nor prompt-submission authority has been independently verified.",
                false);
        }
        catch (JsonException) { return Unavailable("INVALID_RELAY_METADATA"); }
    }

    private static bool NonnegativeCount(JsonElement json, string field, out int value)
    {
        value = 0;
        return json.TryGetProperty(field, out var item)
               && item.ValueKind == JsonValueKind.Number
               && item.TryGetInt32(out value)
               && value >= 0;
    }

    private static ChatGptRelayState Unavailable(string reason) =>
        new(Schema, false, "NOT_CONNECTED", null, 0, 0, 0, 0, reason,
            "No validated real ChatGPT browser session evidence was obtained.", false);
}

public sealed record ChatGptRelayState(
    string Schema,
    bool Connected,
    string State,
    string? CanonicalConversationUrl,
    int MatchingReadyClients,
    int QueueDepth,
    int QuarantinedClients,
    int QuarantinedConversations,
    string ErrorCode,
    string EvidenceBoundary,
    bool MaySubmit);
