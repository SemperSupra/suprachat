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
    private const int MaxBodyBytes = 65536;
    private static readonly TimeSpan CompleteReadDeadline = TimeSpan.FromSeconds(4);

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
        // ResponseHeadersRead stops HttpClient.Timeout at the headers.
        // A separate deadline must cover the ENTIRE response body, otherwise
        // a stalled localhost server can retain the desktop indefinitely.
        using var client = new HttpClient(handler) {Timeout = CompleteReadDeadline};
        using var deadline = new CancellationTokenSource(CompleteReadDeadline);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"http://127.0.0.1:{port}/v1/sessions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure("LEDGER_HOST_REJECTED_OR_UNAVAILABLE");
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                return Failure("LEDGER_RESPONSE_TOO_LARGE");
            await using var incoming = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await incoming.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (bytes.Length + count > MaxBodyBytes)
                    return Failure("LEDGER_RESPONSE_TOO_LARGE");
                bytes.Write(buffer, 0, count);
            }
            using var parsed = JsonDocument.Parse(bytes.ToArray(),
                new JsonDocumentOptions {MaxDepth = 12});
            var inventory = parsed.RootElement.Deserialize<SessionLedgerInventory>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (inventory is null || inventory.Schema != SharedSessionLedger.Schema ||
                inventory.Sessions is null || inventory.Events is null ||
                inventory.Sessions.Count > SharedSessionLedger.MaxSessions ||
                inventory.Events.Count > SharedSessionLedger.MaxEvents ||
                inventory.Sequence < 0 ||
                (inventory.HostInstanceId is not null &&
                 (!Guid.TryParseExact(inventory.HostInstanceId, "N", out var parsedHost) ||
                  !string.Equals(parsedHost.ToString("N"), inventory.HostInstanceId,
                      StringComparison.Ordinal))) ||
                inventory.AuthenticatedAudiences ||
                inventory.ChatSubmissionAllowed || inventory.TerminalExecutionAllowed)
                return Failure("LEDGER_SCOPE_OR_SCHEMA_UNVERIFIED");

            // A bearer-protected loopback response is still *untrusted*
            // process data. Reject impossible identities, nulls, privilege
            // claims and duplicate sessions rather than displaying them.
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in inventory.Sessions)
            {
                if (item is null || item.Id is null ||
                    !Guid.TryParseExact(item.Id, "N", out _) ||
                    !seenIds.Add(item.Id) ||
                    string.IsNullOrWhiteSpace(item.Title) ||
                    item.Title.Length > 80 ||
                    item.Version is < 0 ||
                    item.ChatSubmissionAllowed ||
                    item.TerminalExecutionAllowed ||
                    !string.Equals(item.RemoteBindingState, "NO_REMOTE_BINDING",
                        StringComparison.Ordinal))
                    return Failure("LEDGER_INVALID_SESSION_METADATA");
            }
            long lastSequence = 0;
            var retainedEvents = new List<SharedSessionEventRow>(inventory.Events.Count);
            foreach (var item in inventory.Events)
            {
                if (item is null || item.Sequence <= lastSequence ||
                    item.Sequence > inventory.Sequence ||
                    !Guid.TryParseExact(item.SessionId, "N", out _) ||
                    !seenIds.Contains(item.SessionId) ||
                    item.SessionVersion < 0 ||
                    item.Audience is not ("human" or "automation" or "agent") ||
                    item.Operation is not ("open" or "set-title" or "set-workspace" or
                        "set-conversation" or "revoke-own-grant"))
                    return Failure("LEDGER_INVALID_EVENT_SEQUENCE");
                retainedEvents.Add(new SharedSessionEventRow(item.Sequence,
                    item.SessionId,item.Audience,item.Operation,item.SessionVersion));
                lastSequence = item.Sequence;
            }
            // Bind neither desktop tabs nor terminal handles to this
            // inventory. Sanitize text even when server claimed it is safe.
            var display = inventory.Sessions.Select(item => new SharedSessionInventoryRow(
                item.Id, Sanitize(item.Title), item.Version,
                item.ConversationUrl is not null, "NO_REMOTE_BINDING"))
                .ToArray();
            return new(Schema,true,"UNVERIFIED_LOOPBACK_HOST_SELF_REPORT",
                inventory.Sequence,display,
                ReadOnly:true, BoundToDesktopSessions:false,
                ActorIdentityAuthenticated:false,
                // Report the host's unverified self-description, not proof of
                // independent storage qualification or deployment admission.
                DurableAcrossRestart:inventory.DurableAcrossProcessRestart,
                ChatSubmissionAllowed:false,TerminalExecutionAllowed:false,
                Events:retainedEvents,
                HostInstanceId:inventory.HostInstanceId);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
            or IOException or JsonException or InvalidOperationException or NotSupportedException
            or ArgumentException)
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
    string RemoteBindingState)
{
    // Disambiguate same-titled sessions for screen readers and keyboard users.
    public string DisplayLabel => $"{Title} — {Id[..8]}, revision {Version}";
}

public sealed record SharedSessionEventRow(
    long Sequence,string SessionId,string Audience,string Operation,long SessionVersion)
{
    public string DisplayLabel =>
        $"Event {Sequence}: {Audience} {Operation}, revision {SessionVersion}";
}

public sealed record SharedSessionInventoryReceipt(
    string Schema,bool Connected,string Status,long Sequence,
    IReadOnlyList<SharedSessionInventoryRow> Sessions,
    bool ReadOnly,bool BoundToDesktopSessions,
    bool ActorIdentityAuthenticated,bool DurableAcrossRestart,
    bool ChatSubmissionAllowed,bool TerminalExecutionAllowed,
    IReadOnlyList<SharedSessionEventRow>? Events = null,
    // Null for legacy W6 observations: no continuity assertion allowed.
    string? HostInstanceId = null)
{
    public IReadOnlyList<SharedSessionEventRow> RetainedEvents =>
        Events ?? Array.Empty<SharedSessionEventRow>();
}
