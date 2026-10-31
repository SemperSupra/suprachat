namespace SupraChat.Core;

/// <summary>
/// One semantic entry point for the Sessions workbench, independent of
/// Avalonia, IPC, transport details or a client-declared audience.
/// Product implementations must authenticate the caller at the owning host.
/// </summary>
public interface IWorkbenchSessionAuthority
{
    ValueTask<LedgerWorkbenchReadReceipt> ReadAsync(string? selectedSessionId = null, CancellationToken token = default);
    ValueTask<SessionLedgerState?> InspectAsync(string sessionId, CancellationToken token = default);
    ValueTask<SessionLedgerResult> CreateAsync(string operationId, string title,
        string? conversationUrl = null, string? workspaceKey = null, CancellationToken token = default);
    ValueTask<SessionLedgerResult> RenameAsync(string operationId, string sessionId,
        long expectedVersion, string title, CancellationToken token = default);
}

/// <summary>
/// Transitional W0 in-process metadata adapter. This does not authenticate
/// human identity, persist sessions, or share state across processes. It must
/// be replaced by an admitted external owner before S0 release qualification.
/// Construction belongs to the application composition root, never a view.
/// </summary>
public sealed class ExperimentalLocalWorkbenchSessionAuthority : IWorkbenchSessionAuthority
{
    private readonly SharedSessionLedger _ledger = new();

    public ValueTask<LedgerWorkbenchReadReceipt> ReadAsync(string? selectedSessionId = null, CancellationToken token = default) =>
        ValueTask.FromResult(LedgerWorkbenchProjection.Read(_ledger, selectedSessionId));

    public ValueTask<SessionLedgerState?> InspectAsync(string sessionId, CancellationToken token = default) =>
        ValueTask.FromResult(_ledger.Inspect(sessionId));

    public ValueTask<SessionLedgerResult> CreateAsync(string operationId, string title,
        string? conversationUrl = null, string? workspaceKey = null, CancellationToken token = default) =>
        ValueTask.FromResult(_ledger.Open(operationId, "human", title, conversationUrl, workspaceKey));

    public ValueTask<SessionLedgerResult> RenameAsync(string operationId, string sessionId,
        long expectedVersion, string title, CancellationToken token = default) =>
        ValueTask.FromResult(_ledger.Change(operationId, "human", sessionId, expectedVersion, "set-title", title));
}
