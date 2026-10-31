using System.Text.Json;
using SupraChat.Core;

namespace SupraChat.Automation;

/// <summary>
/// Legacy W6 test-facing receipt wrapper. GUI, CLI and JSON-RPC now share
/// one synthetic HTTP transport implementation in SupraChat.Core.
/// </summary>
public static class SessionLedgerClient
{
    public const string Schema = "suprachat-w6-loopback-ledger-client/v1";

    public static async Task<LedgerClientReceipt> RequestAsync(
        int port, string operation, JsonElement? input = null)
    {
        var response = await W6SyntheticSessionTransport.RequestAsync(port, operation, input)
            .ConfigureAwait(false);
        return new(Schema, operation, port, response.HttpStatus,
            response.Succeeded, response.Payload,
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
