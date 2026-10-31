using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// Explicit opt-in disposable adapter for proving that the *normal* W1 GUI
/// can share one W6 host with independent CLI and JSON-RPC processes.
/// The loopback bearer does NOT prove human, automation or agent identity.
/// Never use this adapter with real credentials or provider execution.
/// </summary>
public sealed class W6SyntheticWorkbenchSessionAuthority : IWorkbenchSessionAuthority
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly int _port;

    public W6SyntheticWorkbenchSessionAuthority(int port)
    {
        if (Environment.GetEnvironmentVariable(W6SyntheticSessionTransport.DisposableEnvironment) != "yes")
            throw new InvalidOperationException("W6_SYNTHETIC_DISPOSABLE_ONLY");
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }

    public async ValueTask<LedgerWorkbenchReadReceipt> ReadAsync(
        string? selectedSessionId = null, CancellationToken token = default)
    {
        var inventory = await InventoryAsync(token).ConfigureAwait(false);
        return LedgerWorkbenchProjection.FromInventory(inventory, selectedSessionId);
    }

    public async ValueTask<SessionLedgerState?> InspectAsync(
        string sessionId, CancellationToken token = default)
    {
        var inventory = await InventoryAsync(token).ConfigureAwait(false);
        return inventory.Sessions.FirstOrDefault(s =>
            string.Equals(s.Id, sessionId, StringComparison.Ordinal));
    }

    public ValueTask<SessionLedgerResult> CreateAsync(
        string operationId, string title, string? conversationUrl = null,
        string? workspaceKey = null, CancellationToken token = default) =>
        CommandAsync("open", JsonSerializer.SerializeToElement(new
        {
            idempotencyKey = operationId,
            audience = "human",
            title,
            conversationUrl,
            workspaceKey
        }), token);

    public ValueTask<SessionLedgerResult> RenameAsync(
        string operationId, string sessionId, long expectedVersion,
        string title, CancellationToken token = default) =>
        CommandAsync("change", JsonSerializer.SerializeToElement(new
        {
            idempotencyKey = operationId,
            audience = "human",
            sessionId,
            expectedVersion,
            operation = "set-title",
            value = title
        }), token);

    private async Task<SessionLedgerInventory> InventoryAsync(CancellationToken token)
    {
        var response = await W6SyntheticSessionTransport.RequestAsync(
            _port, "list", token: token).ConfigureAwait(false);
        if (!response.Succeeded || response.Payload is null)
            throw new InvalidOperationException("W6_SYNTHETIC_OWNER_UNAVAILABLE");
        return response.Payload.Value.Deserialize<SessionLedgerInventory>(Json)
            ?? throw new InvalidDataException("INVALID_LEDGER_INVENTORY");
    }

    private async ValueTask<SessionLedgerResult> CommandAsync(
        string operation, JsonElement payload, CancellationToken token)
    {
        var response = await W6SyntheticSessionTransport.RequestAsync(
            _port, operation, payload, token).ConfigureAwait(false);
        if (response.Payload is null)
            throw new InvalidOperationException("W6_SYNTHETIC_OWNER_UNAVAILABLE");
        return response.Payload.Value.Deserialize<SessionLedgerResult>(Json)
            ?? throw new InvalidDataException("INVALID_LEDGER_RESULT");
    }
}
