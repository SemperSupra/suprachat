using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// Disposable W6 HTTP transport shared by synthetic GUI, CLI and agent adapters.
/// This is deliberately NOT trusted OS-peer admission or production storage.
/// </summary>
public static class W6SyntheticSessionTransport
{
    public const string DisposableEnvironment = "SUPRACHAT_W6_DISPOSABLE_ONLY";
    public const string TokenEnvironment = "SUPRACHAT_LEDGER_TOKEN";
    private const int MaximumRequestBytes = 8 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;

    public static async Task<W6SyntheticSessionHttpResult> RequestAsync(
        int port, string operation, JsonElement? input = null, CancellationToken token = default)
    {
        if (Environment.GetEnvironmentVariable(DisposableEnvironment) != "yes")
            throw new InvalidOperationException("W6_SYNTHETIC_DISPOSABLE_ONLY");
        if (port is < 1024 or > 65535)
            throw new ArgumentException("INVALID_EXPLICIT_LEDGER_PORT", nameof(port));
        if (operation is not ("list" or "open" or "change"))
            throw new ArgumentException("INVALID_LEDGER_CLIENT_OPERATION", nameof(operation));

        var bearer = Environment.GetEnvironmentVariable(TokenEnvironment);
        if (bearer is null || bearer.Length is < 32 or > 128 ||
            bearer.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException("LEDGER_CLIENT_TOKEN_REQUIRED");

        var path = operation switch
        {
            "list" => "/v1/sessions",
            "open" => "/v1/sessions/open",
            "change" => "/v1/sessions/change",
            _ => throw new InvalidOperationException("UNREACHABLE_OPERATION")
        };
        var body = input?.GetRawText();
        if (operation != "list" && string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("LEDGER_REQUEST_BODY_REQUIRED", nameof(input));
        if (body is not null && Encoding.UTF8.GetByteCount(body) > MaximumRequestBytes)
            throw new ArgumentException("LEDGER_REQUEST_SIZE_EXCEEDED", nameof(input));

        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        using var request = new HttpRequestMessage(
            operation == "list" ? HttpMethod.Get : HttpMethod.Post,
            new Uri($"http://127.0.0.1:{port}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("LEDGER_RESPONSE_TOO_LARGE");

        await using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await content.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("LEDGER_RESPONSE_TOO_LARGE");
            output.Write(buffer, 0, read);
        }

        JsonElement? payload = null;
        if (output.Length > 0)
        {
            using var document = JsonDocument.Parse(output.ToArray());
            payload = document.RootElement.Clone();
        }
        return new((int)response.StatusCode, response.IsSuccessStatusCode, payload);
    }
}

public sealed record W6SyntheticSessionHttpResult(
    int HttpStatus, bool Succeeded, JsonElement? Payload);
