using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using SupraChat.Core;

namespace SupraChat.Automation;

/// <summary>
/// W6 experimental loopback transport to one in-memory SharedSessionLedger.
/// Explicit operator-start only; ephemeral state; no browser/terminal actions.
/// A bearer token gates all routes, but audience strings are untrusted labels.
/// </summary>
public static class SessionLedgerHost
{
    public const string Schema = "suprachat-w6-loopback-session-host/v1";
    public const string TokenEnvironment = "SUPRACHAT_LEDGER_TOKEN";

    public static async Task RunAsync(int port)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Explicit nonprivileged port required");
        var token = Environment.GetEnvironmentVariable(TokenEnvironment);
        if (token is null || token.Length is < 32 or > 128 ||
            token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException("LEDGER_HOST_TOKEN_REQUIRED");
        var tokenBytes = Encoding.ASCII.GetBytes(token);
        // W9 is explicitly opt-in, metadata-only disposable local RDTE.
        // No implicit migration of existing ephemeral W6 users or secrets.
        var storePath = Environment.GetEnvironmentVariable("SUPRACHAT_LEDGER_STORE");
        using var durableStore = string.IsNullOrWhiteSpace(storePath) ? null :
            new W9DurableSessionStore(storePath);
        var ledger = new SharedSessionLedger(
            durableStore?.Load(),
            durableStore is null ? null : (Action<SessionLedgerCheckpoint>)durableStore.Save);

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Explicit IPv4 loopback only. No wildcard binding, DNS, or
            // automatic interface search. External network not needed.
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxRequestBodySize = 8 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote) ||
                context.Request.Host.Host != "127.0.0.1" ||
                context.Request.Host.Port != port ||
                context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var header = context.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.Ordinal)
                ? header["Bearer ".Length..] : string.Empty;
            var candidate = Encoding.ASCII.GetBytes(supplied);
            if (candidate.Length != tokenBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(candidate, tokenBytes))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next().ConfigureAwait(false);
        });

        app.MapGet("/v1/health", () => Results.Json(new
        {
            schema = Schema, transport = "HTTP_LOOPBACK_BEARER",
            address = "127.0.0.1",
            port, audience_identity_authenticated = false,
            durable_recovery_available = durableStore is not null,
            chat_submission_allowed = false,
            terminal_execution_allowed = false
        }));
        app.MapGet("/v1/sessions", () => Results.Json(
            ledger.InspectAll() with
            {
                DurableAcrossProcessRestart = durableStore is not null,
                CrossProcessTransportAvailable = true
            }));
        app.MapGet("/v1/sessions/{sessionId}", (string sessionId) =>
        {
            var snapshot = ledger.Inspect(sessionId);
            return snapshot is null ? Results.NotFound() : Results.Json(snapshot);
        });
        app.MapPost("/v1/sessions/open", async (HttpContext context) =>
        {
            try
            {
                var body = await context.Request.ReadFromJsonAsync<OpenPayload>();
                if (body is null) return Results.BadRequest(new {error = "INVALID_REQUEST_BODY"});
                return ToHttpResult(ledger.Open(
                    body.IdempotencyKey!, body.Audience!, body.Title!,
                    body.ConversationUrl, body.WorkspaceKey));
            }
            catch (ArgumentException) { return Results.BadRequest(new {error = "INVALID_METADATA"}); }
            catch (JsonException) { return Results.BadRequest(new {error = "INVALID_JSON"}); }
        });
        app.MapPost("/v1/sessions/change", async (HttpContext context) =>
        {
            try
            {
                var body = await context.Request.ReadFromJsonAsync<ChangePayload>();
                if (body is null) return Results.BadRequest(new {error = "INVALID_REQUEST_BODY"});
                return ToHttpResult(ledger.Change(
                    body.IdempotencyKey!, body.Audience!, body.SessionId!,
                    body.ExpectedVersion, body.Operation!, body.Value));
            }
            catch (ArgumentException) { return Results.BadRequest(new {error = "INVALID_METADATA"}); }
            catch (JsonException) { return Results.BadRequest(new {error = "INVALID_JSON"}); }
        });

        await app.StartAsync().ConfigureAwait(false);
        // The only stdout receipt; do not print token, client data or metadata.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = Schema, bound_address = "127.0.0.1", port,
            bearer_token_required = true,
            authenticated_audience_identity = false,
            cross_process_transport_available = true,
            persistent_recovery_available = durableStore is not null,
            provider_prompt_submission_available = false
        }));
        Console.Out.Flush();
        await app.WaitForShutdownAsync().ConfigureAwait(false);
    }

    private static IResult ToHttpResult(SessionLedgerResult result)
    {
        var status = result.Applied ? StatusCodes.Status200OK : result.Status switch
        {
            "SESSION_NOT_FOUND" => StatusCodes.Status404NotFound,
            "IDEMPOTENCY_CAPACITY_REACHED" => StatusCodes.Status429TooManyRequests,
            "SESSION_CAPACITY_REACHED" => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status409Conflict
        };
        return Results.Json(result, statusCode: status);
    }

    public sealed record OpenPayload(
        string? IdempotencyKey, string? Audience, string? Title,
        string? ConversationUrl, string? WorkspaceKey);

    public sealed record ChangePayload(
        string? IdempotencyKey, string? Audience, string? SessionId,
        long ExpectedVersion, string? Operation, string? Value);
}
