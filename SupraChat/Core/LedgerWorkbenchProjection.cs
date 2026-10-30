namespace SupraChat.Core;

/// <summary>
/// Read-only, metadata-only view of the already accepted session ledger.
/// Never creates a session, ID, credential, process, or alternate write authority.
/// </summary>
public static class LedgerWorkbenchProjection
{
    public const string Schema = "suprachat-ledger-workbench-projection/v1";

    public static LedgerWorkbenchReadReceipt Read(SharedSessionLedger ledger, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return FromInventory(ledger.InspectAll(), selectedId);
    }

    // A caller-supplied inventory is NOT evidence of actor identity or host origin.
    public static LedgerWorkbenchReadReceipt FromInventory(
        SessionLedgerInventory inventory, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        LedgerWorkbenchReadReceipt Deny(string reason) =>
            new(Schema, reason, null, inventory.Sequence, false, false, false);
        if (inventory.Schema != SharedSessionLedger.Schema || inventory.Sequence < 0 ||
            inventory.Sessions is null || inventory.Events is null ||
            inventory.DurableAcrossProcessRestart || inventory.AuthenticatedAudiences ||
            inventory.CrossProcessTransportAvailable || inventory.ChatSubmissionAllowed ||
            inventory.TerminalExecutionAllowed)
            return Deny("SOURCE_CAPABILITY_UNQUALIFIED");
        if (inventory.Sessions.Count > SessionWorkbench.MaximumSessions)
            return Deny("WORKBENCH_CAPACITY_UNSUPPORTED");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sessions = new List<WorkbenchSession>();
        foreach (var entry in inventory.Sessions)
        {
            if (entry is null || !Guid.TryParseExact(entry.Id, "N", out _) ||
                !ids.Add(entry.Id) || string.IsNullOrWhiteSpace(entry.Title) ||
                entry.Title.Length > 80 || entry.Title.Any(char.IsControl) ||
                entry.Version < 0 || entry.ChatSubmissionAllowed || entry.TerminalExecutionAllowed)
                return Deny("INVALID_LEDGER_SESSION");
            if (entry.ConversationUrl is not null)
            {
                var check = ChatGptControlContract.Inspect(entry.ConversationUrl);
                if (!check.Recognized || check.CanonicalUrl != entry.ConversationUrl)
                    return Deny("INVALID_CONVERSATION_REFERENCE");
            }
            if (entry.WorkspaceKey is not null &&
                (entry.WorkspaceKey.Length > 512 ||
                 string.IsNullOrWhiteSpace(entry.WorkspaceKey) ||
                 entry.WorkspaceKey.Any(char.IsControl)))
                return Deny("INVALID_WORKSPACE_REFERENCE");
            sessions.Add(new(entry.Id, entry.Title, entry.ConversationUrl,
                entry.WorkspaceKey, "NOT_CONNECTED", false, false));
        }

        var collisions = new List<WorkbenchCollision>();
        for (var i = 0; i < sessions.Count; i++)
        for (var j = i + 1; j < sessions.Count; j++)
        {
            var a = sessions[i];
            var b = sessions[j];
            if (a.ConversationUrl is not null && a.ConversationUrl == b.ConversationUrl)
                collisions.Add(new("DUPLICATE_CONVERSATION", a.Id, b.Id));
            if (a.WorkspaceKey is not null &&
                string.Equals(a.WorkspaceKey, b.WorkspaceKey, StringComparison.OrdinalIgnoreCase))
                collisions.Add(new("SHARED_WORKSPACE", a.Id, b.Id));
        }

        var stale = selectedId is not null && !ids.Contains(selectedId);
        var snapshot = new WorkbenchSnapshot(SessionWorkbench.Schema,
            stale ? null : selectedId, sessions.ToArray(), collisions.ToArray(),
            SessionWorkbench.MaximumSessions, false, false);
        return new(Schema, stale ? "SESSION_SELECTION_STALE" : "OBSERVED",
            snapshot, inventory.Sequence, true, false, false);
    }
}

public sealed record LedgerWorkbenchReadReceipt(
    string Schema, string Status, WorkbenchSnapshot? Snapshot, long LedgerSequence,
    bool ReadOnly, bool ChatSubmissionPermitted, bool TerminalExecutionPermitted);
