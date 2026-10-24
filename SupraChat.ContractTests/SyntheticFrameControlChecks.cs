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
}
