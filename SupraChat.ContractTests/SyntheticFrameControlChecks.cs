using SupraChat.Core;

internal static class SyntheticFrameControlChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Unauthorized simulation action accepted.");
    }

    private static void Conflict(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected bounded/idempotency conflict.");
    }

    public static void Run()
    {
        foreach (TraceOperation op in Enum.GetValues<TraceOperation>())
        {
            Require(TraceControlBoundary.CanManageMetadata(op) == TracePolicy.Allows(op),
                "Metadata operation classification drift.");
            foreach (TraceSurface surface in Enum.GetValues<TraceSurface>())
                Require(!TraceControlBoundary.IsActuationAdmitted(op, surface),
                    "A provider or simulated stream acquired unreviewed authority.");
        }
        foreach (var blocked in new[] { TraceOperation.CancelProvider,
            TraceOperation.CapturePayload, TraceOperation.ModifyFirstParty })
            Require(TraceControlBoundary.RequiresSeparateAuthorization(blocked),
                "Consequential operation incorrectly admitted.");
        Require(!TraceControlBoundary.CanManageMetadata((TraceOperation)999),
            "Unknown operation accepted.");

        using var lab = new SyntheticFrameSimulator(maxFixtures: 2, maxOutcomes: 4);
        var first = lab.Open();
        var second = lab.Open();
        Conflict(() => lab.Open());

        var operationId = Guid.NewGuid();
        var firstResult = lab.Apply(first, operationId, SyntheticFrameTreatment.DropLast, 3);
        Require(firstResult.InputFrames == 3 && firstResult.OutputFrames == 2 &&
                !firstResult.SimulatedFailure && firstResult.FirstExecutionOrdinal == 1 &&
                firstResult.EvidenceClass == "SYNTHETIC_ONLY" &&
                !firstResult.ProviderOperationVerified, "Synthetic drop proof incorrect.");
        Require(ReferenceEquals(firstResult,
                lab.Apply(first, operationId, SyntheticFrameTreatment.DropLast, 3)),
            "Exact retry changed immutable first outcome.");
        Conflict(() => lab.Apply(first, operationId, SyntheticFrameTreatment.RepeatLast, 3));
        Conflict(() => lab.Apply(first, operationId, SyntheticFrameTreatment.DropLast, 4));

        var replay = lab.Apply(first, Guid.NewGuid(), SyntheticFrameTreatment.RepeatLast, 64);
        Require(replay.OutputFrames == 65 && replay.FirstExecutionOrdinal == 2,
            "Synthetic replay count incorrect.");
        var failed = lab.Apply(first, Guid.NewGuid(), SyntheticFrameTreatment.FailBeforeDelivery, 2);
        Require(failed.OutputFrames == 0 && failed.SimulatedFailure,
            "Synthetic pre-delivery error not represented.");
        Reject(() => lab.Apply(first, Guid.Empty, SyntheticFrameTreatment.DropLast, 2));
        Reject(() => lab.Apply(first, Guid.NewGuid(), SyntheticFrameTreatment.DropLast, 0));
        Reject(() => lab.Apply(first, Guid.NewGuid(), SyntheticFrameTreatment.DropLast, 65));
        Reject(() => lab.Apply(first, Guid.NewGuid(), (SyntheticFrameTreatment)99, 2));

        var secondResult = lab.Apply(second, operationId, SyntheticFrameTreatment.DropLast, 3);
        Require(secondResult.FirstExecutionOrdinal == 1 &&
                !ReferenceEquals(firstResult, secondResult), "Fixture sessions shared results.");
        lab.Apply(first, Guid.NewGuid(), SyntheticFrameTreatment.DropLast, 1);
        Conflict(() => lab.Apply(first, Guid.NewGuid(), SyntheticFrameTreatment.DropLast, 1));

        using var another = new SyntheticFrameSimulator();
        var foreign = another.Open();
        Reject(() => lab.Apply(foreign, Guid.NewGuid(), SyntheticFrameTreatment.DropLast, 3));
        Reject(() => another.Apply(first, Guid.NewGuid(), SyntheticFrameTreatment.DropLast, 3));
        lab.Close(second);
        Reject(() => lab.Apply(second, operationId, SyntheticFrameTreatment.DropLast, 3));
        var replacement = lab.Open();
        Require(lab.Apply(replacement, operationId, SyntheticFrameTreatment.DropLast, 3)
                .FirstExecutionOrdinal == 1, "Closed fixture contaminated replacement.");

        lab.Dispose();
        try { lab.Apply(first, operationId, SyntheticFrameTreatment.DropLast, 3); }
        catch (ObjectDisposedException)
        {
            Console.WriteLine("T3 synthetic stream-control checks PASS (no provider actuation)");
            return;
        }
        throw new InvalidOperationException("Disposed simulator retained operation authority.");
    }

    public static async Task RunTraceFixtureAsync()
    {
        using var lab = new SyntheticFrameSimulator();
        var fixture = lab.Open();
        var dropped = lab.Apply(fixture, Guid.NewGuid(), SyntheticFrameTreatment.DropLast, 3);
        var repeated = lab.Apply(fixture, Guid.NewGuid(), SyntheticFrameTreatment.RepeatLast, 2);
        var failed = lab.Apply(fixture, Guid.NewGuid(), SyntheticFrameTreatment.FailBeforeDelivery, 1);

        using var trace = new TraceNormalizer();
        var first = trace.CreateSession();
        var second = trace.CreateSession();
        var third = trace.CreateSession();
        trace.SetCapture(first, TraceCaptureMode.Metadata);
        trace.SetCapture(second, TraceCaptureMode.Metadata);
        trace.SetCapture(third, TraceCaptureMode.Metadata);
        await Task.WhenAll(
            trace.PumpAsync(first, new SyntheticFrameTraceSource(dropped)),
            trace.PumpAsync(second, new SyntheticFrameTraceSource(repeated)),
            trace.PumpAsync(third, new SyntheticFrameTraceSource(failed)));
        var droppedEvents = trace.Read(first);
        var repeatedEvents = trace.Read(second);
        var failedEvents = trace.Read(third);

        Require(droppedEvents.Events.Count == 5 &&
                droppedEvents.Events.Count(e => e.EventKind == TraceEventKind.Chunk) == 2 &&
                droppedEvents.Events.Last().EventKind == TraceEventKind.CaptureGap &&
                droppedEvents.Events.All(e => e.EventKind != TraceEventKind.Completed) &&
                droppedEvents.CaptureGap, "Dropped synthetic frame appeared as completed trace.");
        var repeatedChunks = repeatedEvents.Events.Where(e => e.EventKind == TraceEventKind.Chunk).ToArray();
        Require(repeatedEvents.Events.Count == 7 && repeatedChunks.Length == 3 &&
                repeatedChunks[1].SourceSequence == repeatedChunks[2].SourceSequence &&
                repeatedEvents.Events[^1].EventKind == TraceEventKind.Completed &&
                repeatedEvents.CaptureGap && repeatedEvents.Completeness == "UNKNOWN",
                "Replayed synthetic source event or capture gap was concealed.");
        Require(failedEvents.Events.Select(e => e.EventKind).SequenceEqual(new[] {
                TraceEventKind.Request, TraceEventKind.Error, TraceEventKind.CaptureGap }) &&
                failedEvents.CaptureGap, "Synthetic pre-delivery error was not traced.");
        Require(droppedEvents.Events.All(e => e.SessionId == first.SessionId) &&
                repeatedEvents.Events.All(e => e.SessionId == second.SessionId) &&
                failedEvents.Events.All(e => e.SessionId == third.SessionId),
                "Interleaved synthetic frames crossed a session boundary.");
        var exported = System.Text.Json.JsonSerializer.Serialize(new[] {
            droppedEvents, repeatedEvents, failedEvents }, TraceNormalizer.JsonOptions);
        Require(!exported.Contains("synthetic-only-operation", StringComparison.Ordinal) &&
                !exported.Contains("http://", StringComparison.Ordinal),
                "Synthetic trace export leaked raw source identifiers.");
        Console.WriteLine("T3 synthetic trace-stream composition PASS (metadata only)");
    }

}
