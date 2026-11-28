namespace SupraChat.Core;

/// <summary>Retains an ambiguous close request for explicit retry within this workbench.</summary>
public sealed class WorkbenchSessionClose(IWorkbenchSessionAuthority authority)
{
    private readonly Dictionary<string, CloseRequest> _unknown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    public string[] PendingSessions() => _unknown.Keys.Where(id => !_inFlight.Contains(id)).ToArray();

    public async ValueTask<WorkbenchCloseResult> ExecuteAsync(string sessionId, Func<bool> stillSelected,
        bool retryOnly = false)
    {
        if (!_inFlight.Add(sessionId))
            return new("CLOSE_PENDING");
        try
        {
            if (!stillSelected())
                return new("SESSION_SELECTION_STALE");
            var retry = _unknown.TryGetValue(sessionId, out var request);
            if (!retry || request is null)
            {
                if (retryOnly) return new("NO_PENDING_CLOSE");
                SessionLedgerState? existing;
                try { existing = await authority.InspectAsync(sessionId); }
                catch (Exception) { return new("OWNER_UNAVAILABLE"); }
                if (!stillSelected() || existing is null || existing.Id != sessionId || existing.IsArchived)
                    return new("SESSION_SELECTION_STALE");
                request = new("workbench-ui-" + Guid.NewGuid().ToString("N"), existing.Version);
            }

            _unknown[sessionId] = request;
            SessionLedgerResult outcome;
            try { outcome = await authority.CloseAsync(request.OperationId, sessionId, request.Version); }
            catch (Exception) { return new("OUTCOME_UNKNOWN"); }
            if (outcome.Schema != SharedSessionLedger.Schema ||
                (outcome.Session is { } returned && returned.Id != sessionId))
                return new("OUTCOME_UNKNOWN");
            if (!outcome.Applied && outcome.Status is
                "VERSION_CONFLICT" or "SESSION_NOT_FOUND" or "IDEMPOTENCY_CAPACITY_REACHED" or
                "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT" or "GRANT_REVOKED")
            {
                _unknown.Remove(sessionId);
                return new(outcome.Status);
            }
            if (!outcome.Applied || outcome.Status != "APPLIED" ||
                outcome.Session is not { IsArchived: true } archived ||
                archived.Version <= 0 || archived.Version - 1 != request.Version)
                return new("OUTCOME_UNKNOWN");

            SessionLedgerState? observed;
            try { observed = await authority.InspectAsync(sessionId); }
            catch (Exception) { return new("OUTCOME_UNKNOWN"); }
            if (observed is null || observed.Id != sessionId)
                return new("OUTCOME_UNKNOWN");

            _unknown.Remove(sessionId);
            return observed.IsArchived && observed.Version == archived.Version
                ? new("ARCHIVED", !retry && !outcome.IsReplay && stillSelected())
                : new("ARCHIVE_SUPERSEDED");
        }
        finally { _inFlight.Remove(sessionId); }
    }

    private sealed record CloseRequest(string OperationId, long Version);
}

public sealed record WorkbenchCloseResult(string Status, bool MayDiscardDrafts = false);
