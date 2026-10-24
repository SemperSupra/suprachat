namespace SupraChat.Core;

/// <summary>
/// Pure, read-only UI/CLI projection of a bounded W6 host observation.
/// Selection belongs to this observer, NEVER the local workbench or browser.
/// Sequence changes detect probable catch-up gaps; they do not prove the
/// network capture is continuous or that ChatGPT turns were mirrored.
/// </summary>
public static class SharedSessionFeedProjection
{
    public const string Schema = "suprachat-w14-shared-session-feed-observer/v1";
    private const int MaxVisibleEvents = 12;

    public static SharedSessionFeedView Project(
        SharedSessionInventoryReceipt observation, string? selectedSessionId,
        long? previousSequence)
    {
        if (!observation.Connected)
            return View(observation, "HOST_UNAVAILABLE", null, true,
                Array.Empty<SharedSessionEventRow>());
        if (previousSequence is < 0)
            return View(observation, "INVALID_CURSOR", null, true,
                Array.Empty<SharedSessionEventRow>());
        if (previousSequence > observation.Sequence)
            return View(observation, "SEQUENCE_REWIND_RESELECT_REQUIRED",
                null, true, Array.Empty<SharedSessionEventRow>());
        if (selectedSessionId is null)
            return View(observation, "NO_HOST_SESSION_SELECTED", null,
                DetectGap(observation, previousSequence), Array.Empty<SharedSessionEventRow>());
        var selected = observation.Sessions.FirstOrDefault(
            x => string.Equals(x.Id, selectedSessionId, StringComparison.Ordinal));
        if (selected is null)
            return View(observation, "SELECTED_SESSION_MISSING_RESELECT_REQUIRED",
                null, true, Array.Empty<SharedSessionEventRow>());

        var gap = DetectGap(observation, previousSequence);
        // Initial observation is a bounded retained window, NOT complete history.
        // Subsequent reads return strictly new events from the selected host
        // session and never treat a discarded/torn window as full catch-up.
        var selectedEvents = observation.RetainedEvents
            .Where(x => x.SessionId == selected.Id &&
                (previousSequence is null || x.Sequence > previousSequence))
            .TakeLast(MaxVisibleEvents)
            .ToArray();
        var status = gap ? "EVENT_GAP_UNVERIFIED" :
            previousSequence is null ? "SELECTED_RETAINED_WINDOW_ONLY" :
            selectedEvents.Length > 0 ? "SELECTED_NEW_EVENTS" : "SELECTED_NO_NEW_EVENTS";
        return View(observation, status, selected.Id, gap, selectedEvents);
    }

    private static bool DetectGap(SharedSessionInventoryReceipt observed,
        long? cursor)
    {
        if (observed.Sequence == 0) return false;
        if (cursor is null)
            return observed.RetainedEvents.Count == 0 || observed.RetainedEvents[0].Sequence > 1;
        if (cursor >= observed.Sequence) return false;
        var newer = observed.RetainedEvents.Where(e => e.Sequence > cursor).ToArray();
        return newer.Length == 0 || newer[0].Sequence > cursor + 1;
    }

    private static SharedSessionFeedView View(
        SharedSessionInventoryReceipt receipt,string status,
        string? selected,bool gap,IReadOnlyList<SharedSessionEventRow> events) =>
        new(Schema, receipt.Connected, status, receipt.Sequence,
            selected, events, gap, receipt.DurableAcrossRestart,
            ReadOnly: true, BoundToDesktopWorkbench: false,
            RemoteChatAuthority: false, TerminalAuthority: false,
            ActorIdentityAuthenticated: false);
}

public sealed record SharedSessionFeedView(
    string Schema,bool Connected,string Status,long Sequence,
    string? SelectedSessionId,IReadOnlyList<SharedSessionEventRow> Events,
    bool EventGapUnverified,bool HostReportsDurableRestart,
    bool ReadOnly,bool BoundToDesktopWorkbench,
    bool RemoteChatAuthority,bool TerminalAuthority,
    bool ActorIdentityAuthenticated);
