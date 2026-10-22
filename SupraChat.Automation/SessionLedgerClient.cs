using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SupraChat.Automation;

/// <summary>
/// All CLI and JSON-RPC ledger operations use this same fixed loopback client.
/// No DNS, redirect, cookie, proxy, terminal, ChatGPT or browser access.
/// </summary>
public static class SessionLedgerClient
{
    public const string Schema = "suprachat-w6-loopback-ledger-client/v1";
    private const int MaximumBodyBytes = 8 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;

    public static async Task<LedgerClientReceipt> RequestAsync(
        int port, string operation, JsonElement? input = null)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentException("INVALID_EXPLICIT_LEDGER_PORT");
        if (operation is not ("list" or "open" or "change"))
            throw new ArgumentException("INVALID_LEDGER_CLIENT_OPERATION");
        var token = Environment.GetEnvironmentVariable(SessionLedgerHost.TokenEnvironment);
        if (token is null || token.Length is < 32 or > 128 ||
            token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException("LEDGER_CLIENT_TOKEN_REQUIRED");

        var path = operation switch
        {
            "list" => "/v1/sessions",
            "open" => "/v1/sessions/open",
            "change" => "/v1/sessions/change",
            _ => throw new ArgumentException("INVALID_LEDGER_CLIENT_OPERATION")
        };
        var body = input?.GetRawText();
        if (operation != "list" && string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("LEDGER_REQUEST_BODY_REQUIRED");
        if (body is not null && Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes)
            throw new ArgumentException("LEDGER_REQUEST_SIZE_EXCEEDED");

        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) {Timeout = TimeSpan.FromSeconds(4)};
        using var message = new HttpRequestMessage(
            operation == "list" ? HttpMethod.Get : HttpMethod.Post,
            new Uri($"http://127.0.0.1:{port}{path}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("LEDGER_RESPONSE_TOO_LARGE");
        await using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await content.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("LEDGER_RESPONSE_TOO_LARGE");
            output.Write(buffer, 0, read);
        }

        JsonElement? payload = null;
        if (output.Length > 0)
        {
            using var document = JsonDocument.Parse(output.ToArray());
            payload = document.RootElement.Clone();
        }
        return new(Schema, operation, port, (int)response.StatusCode,
            response.IsSuccessStatusCode, payload,
            BearerTokenDisclosed: false,
            ChatSubmissionAttempted: false,
            TerminalCommandExecuted: false,
            AuthenticatedAudienceIdentity: false,
            DurableHostStorage: false);
    }
}

public sealed record LedgerClientReceipt(
    string Schema, string Operation, int Port, int HttpStatus,
    bool Succeeded, JsonElement? Payload, bool BearerTokenDisclosed,
    bool ChatSubmissionAttempted, bool TerminalCommandExecuted,
    bool AuthenticatedAudienceIdentity, bool DurableHostStorage);
