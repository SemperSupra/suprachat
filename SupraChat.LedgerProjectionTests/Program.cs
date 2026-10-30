using SupraChat.Core;

static class Program
{
    static int passed;
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("SINGLE_AUTHORITY_TEST_FAILED_" + name);
        passed++;
    }

    static int Main()
    {
        var ledger = new SharedSessionLedger();
        var a = ledger.Open("workbench-ledger-open-01", "human", "Erste Sitzung",
            workspaceKey: "Synthetic/Workspace");
        var b = ledger.Open("workbench-ledger-open-02", "automation", "ห้องสนทนา",
            workspaceKey: "synthetic\\workspace");
        Check(a.Applied && b.Applied && a.Session is not null &&
              b.Session is not null, "ledger_open");
        var before = ledger.InspectAll();
        var view = LedgerWorkbenchProjection.Read(ledger, a.Session!.Id);
        Check(view.Status == "OBSERVED" && view.ReadOnly, "read_only");
        Check(view.Snapshot?.ActiveSessionId == a.Session.Id, "shared_id");
        Check(view.Snapshot?.Sessions.Select(s => s.Id).OrderBy(x => x)
            .SequenceEqual(before.Sessions.Select(s => s.Id).OrderBy(x => x)) == true,
            "no_second_id_registry");
        Check(view.Snapshot?.Collisions.Single().Type == "SHARED_WORKSPACE",
            "casefold_collision");
        Check(!view.ChatSubmissionPermitted && !view.TerminalExecutionPermitted &&
              !view.Snapshot!.ChatSubmissionPermitted && !view.Snapshot.TerminalExecutionPermitted,
              "no_implicit_provider_or_terminal_effect");
        Check(ledger.InspectAll().Sequence == before.Sequence, "observation_no_mutation");

        var update = ledger.Change("workbench-ledger-change-01", "agent", a.Session.Id,
            a.Session.Version, "set-title", "Aktualisiert");
        Check(update.Applied, "only_ledger_changes_state");
        var updated = LedgerWorkbenchProjection.Read(ledger, a.Session.Id);
        Check(updated.Snapshot?.Sessions.Single(x => x.Id == a.Session.Id).Title ==
              "Aktualisiert", "projection_not_stale");
        Check(updated.LedgerSequence == ledger.InspectAll().Sequence, "source_sequence");
        var stale = LedgerWorkbenchProjection.Read(ledger, Guid.NewGuid().ToString("N"));
        Check(stale.Status == "SESSION_SELECTION_STALE" &&
              stale.Snapshot?.ActiveSessionId is null, "stale_selection_cleared");
        Check(ledger.InspectAll().Sequence == updated.LedgerSequence, "stale_no_mutation");

        var replay = ledger.Open("workbench-ledger-open-01", "human", "Erste Sitzung",
            workspaceKey: "Synthetic/Workspace");
        Check(replay.IsReplay && replay.Session?.Id == a.Session.Id, "replay_unchanged");
        var json = WorkbenchMetadataExport.Prepare(updated.Snapshot!, "json");
        Check(json.Content.Contains("SESSION_METADATA_ONLY") &&
              json.Content.Contains("\"exported_credentials\": false") &&
              !json.WasSaved && json.RequiresUserSelectedDestination, "metadata_only");

        var inventory = ledger.InspectAll();
        Check(LedgerWorkbenchProjection.FromInventory(inventory with {
            AuthenticatedAudiences = true
        }).Status == "SOURCE_CAPABILITY_UNQUALIFIED", "claimed_identity_denied");
        Check(LedgerWorkbenchProjection.FromInventory(inventory with {
            CrossProcessTransportAvailable = true
        }).Status == "SOURCE_CAPABILITY_UNQUALIFIED", "claimed_transport_denied");
        Check(LedgerWorkbenchProjection.FromInventory(inventory with {
            Sessions = new[] { inventory.Sessions[0], inventory.Sessions[0] }
        }).Status == "INVALID_LEDGER_SESSION", "duplicate_id_denied");
        Check(LedgerWorkbenchProjection.FromInventory(inventory with {
            Sessions = new[] { inventory.Sessions[0] with { ChatSubmissionAllowed = true } }
        }).Status == "INVALID_LEDGER_SESSION", "claimed_submit_denied");
        Check(LedgerWorkbenchProjection.FromInventory(inventory with {
            Sessions = new[] { inventory.Sessions[0] with {
                ConversationUrl = "https://untrusted.example/x"
            } }
        }).Status == "INVALID_CONVERSATION_REFERENCE", "foreign_link_denied");
        Check(LedgerWorkbenchProjection.FromInventory(inventory with {
            Sessions = Enumerable.Range(0, 17).Select(_ => inventory.Sessions[0] with {
                Id = Guid.NewGuid().ToString("N")
            }).ToArray()
        }).Status == "WORKBENCH_CAPACITY_UNSUPPORTED", "no_truncation");
        Check(ledger.InspectAll().Sessions.Count == 2, "negatives_are_pure");
        Console.WriteLine($"LEDGER_WORKBENCH_SINGLE_AUTHORITY_PASS cases={passed} " +
            "provider_contact=false cross_process=false actor_auth=false");
        return 0;
    }
}
