using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// W6 bounded, strictly read-only view of an already running localhost ledger.
/// No host start, POST, provider submission, terminal command, or state binding.
/// </summary>
public static class SharedSessionInventoryReader
{
    public const string Schema = "suprachat-w6-desktop-ledger-observer/v1";

    public static async Task<SharedSessionInventoryReceipt> ObserveAsync(
        int port, string? token)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentException("INVALID_LEDGER_LOOPBACK_PORT", nameof(port));
        if (token is null || token.Length is < 32 or > 128 ||
            token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return Failure("LEDGER_BEARER_NOT_CONFIGURED");

        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"http://127.0.0.1:{port}/v1/sessions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure("LEDGER_HOST_REJECTED_OR_UNAVAILABLE");
            if (response.Content.Headers.ContentLength > 65536)
                return Failure("LEDGER_RESPONSE_TOO_LARGE");
            await using var incoming = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await incoming.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count == 0) break;
                if (bytes.Length + count > 65536)
                    return Failure("LEDGER_RESPONSE_TOO_LARGE");
                bytes.Write(buffer, 0, count);
            }
            using var parsed = JsonDocument.Parse(bytes.ToArray(),
                new JsonDocumentOptions {MaxDepth = 12});
            var inventory = parsed.RootElement.Deserialize<SessionLedgerInventory>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (inventory is null || inventory.Schema != SharedSessionLedger.Schema ||
                inventory.Sessions.Count > SharedSessionLedger.MaxSessions ||
                inventory.AuthenticatedAudiences ||
                inventory.DurableAcrossProcessRestart ||
                inventory.ChatSubmissionAllowed || inventory.TerminalExecutionAllowed)
                return Failure("LEDGER_SCOPE_OR_SCHEMA_UNVERIFIED");

            // Bound and sanitize untrusted titles; no terminal, browser or chat
            // data is rendered. Host inventory remains explicitly unbound to UI.
            var display = inventory.Sessions.Select(s => new SharedSessionInventoryRow(
                s.Id, Sanitize(s.Title), s.Version,
                s.ConversationUrl is not null, s.RemoteBindingState))
                .ToArray();
            return new(Schema,true,"UNVERIFIED_LOOPBACK_HOST_SELF_REPORT",
                inventory.Sequence,display,
                ReadOnly:true, BoundToDesktopSessions:false,
                ActorIdentityAuthenticated:false,DurableAcrossRestart:false,
                ChatSubmissionAllowed:false,TerminalExecutionAllowed:false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or IOException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return Failure("LEDGER_READ_FAILED");
        }
    }

    private static string Sanitize(string value) =>
        new string(value.Take(80).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static SharedSessionInventoryReceipt Failure(string reason) =>
        new(Schema,false,reason,0,Array.Empty<SharedSessionInventoryRow>(),
            ReadOnly:true,BoundToDesktopSessions:false,
            ActorIdentityAuthenticated:false,DurableAcrossRestart:false,
            ChatSubmissionAllowed:false,TerminalExecutionAllowed:false);
}

public sealed record SharedSessionInventoryRow(
    string Id,string Title,long Version,bool HasConversationReference,
    string RemoteBindingState);

public sealed record SharedSessionInventoryReceipt(
    string Schema,bool Connected,string Status,long Sequence,
    IReadOnlyList<SharedSessionInventoryRow> Sessions,
    bool ReadOnly,bool BoundToDesktopSessions,
    bool ActorIdentityAuthenticated,bool DurableAcrossRestart,
    bool ChatSubmissionAllowed,bool TerminalExecutionAllowed);
