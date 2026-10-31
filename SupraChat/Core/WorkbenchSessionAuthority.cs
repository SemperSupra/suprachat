namespace SupraChat.Core;

/// <summary>
/// One semantic entry point for the Sessions workbench, independent of
/// Avalonia, IPC, transport details or a client-declared audience.
/// Product implementations must authenticate the caller at the owning host.
/// </summary>
public interface IWorkbenchSessionAuthority
{
    LedgerWorkbenchReadReceipt Read(string? selectedSessionId = null);
    SessionLedgerState? Inspect(string sessionId);
    SessionLedgerResult Create(string operationId, string title,
        string? conversationUrl = null, string? workspaceKey = null);
    SessionLedgerResult Rename(string operationId, string sessionId,
        long expectedVersion, string title);
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

    public LedgerWorkbenchReadReceipt Read(string? selectedSessionId = null) =>
        LedgerWorkbenchProjection.Read(_ledger, selectedSessionId);

    public SessionLedgerState? Inspect(string sessionId) => _ledger.Inspect(sessionId);

    public SessionLedgerResult Create(string operationId, string title,
        string? conversationUrl = null, string? workspaceKey = null) =>
        _ledger.Open(operationId, "human", title, conversationUrl, workspaceKey);

    public SessionLedgerResult Rename(string operationId, string sessionId,
        long expectedVersion, string title) =>
        _ledger.Change(operationId, "human", sessionId, expectedVersion, "set-title", title);
}
