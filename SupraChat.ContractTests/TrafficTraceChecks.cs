using System.Runtime.CompilerServices;
using System.Text.Json;
using SupraChat.Core;

internal static class TrafficTraceChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Refused(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Trace capability or policy accepted an unauthorized operation.");
    }

    public static async Task RunAsync()
    {
        const string secret = "synthetic-secret-sentinel-DO-NOT-EXPORT";
        using var trace = new TraceNormalizer(capacityPerSession: 8, maxSessions: 2);
        var first = trace.CreateSession();
        var second = trace.CreateSession();
        var observation = new TraceObservation(TraceSurface.ResponsesSse, TraceAdapter.Http,
            TraceDirection.Ingress, TraceEventKind.Request,
            $"https://chatgpt.com/c/{secret}?authorization={secret}#fragment",
            secret, secret, SourceSequence: 2, ByteCount: 10);

        Check(!trace.TryObserve(first, observation), "Trace must start OFF.");
        Check(trace.Read(first).Events.Count == 0, "Disabled capture retained metadata.");
        trace.SetCapture(first, TraceCaptureMode.Metadata);
        trace.SetCapture(second, TraceCaptureMode.Metadata);
        Refused(() => trace.SetCapture(first, TraceCaptureMode.PrivilegedPayload));
        Refused(() => trace.SetCapture(first, (TraceCaptureMode)999));
        Check(!TracePolicy.Allows(TraceOperation.CapturePayload) &&
              !TracePolicy.Allows(TraceOperation.ModifyFirstParty) &&
              !TracePolicy.Allows(TraceOperation.CancelProvider) &&
              !TracePolicy.Allows((TraceOperation)999), "Unimplemented actuator authority was granted.");

        // Interleave duplicate/out-of-order SSE with another session's WebSocket and JSON-RPC.
        trace.TryObserve(first, observation);
        trace.TryObserve(second, observation with { ProviderSurface = TraceSurface.ResponsesWebSocket, Adapter = TraceAdapter.WebSocket });
        trace.TryObserve(first, observation with { EventKind = TraceEventKind.Chunk, SourceSequence = 1 });
        trace.TryObserve(second, observation with { ProviderSurface = TraceSurface.CodexJsonRpc, Adapter = TraceAdapter.Stdio });
        trace.TryObserve(first, observation with { EventKind = TraceEventKind.Chunk, SourceSequence = 1 });
        var page = trace.Read(first, limit: 2);
        var next = trace.Read(first, page.NextCursor);
        Check(page.Events.Select(e => e.GlobalSequence).SequenceEqual(new long[] { 1, 3 }) &&
              next.Events.Single().GlobalSequence == 5, "Session pagination skipped or mixed observations.");
        Check(page.Events.All(e => e.SessionId == first.SessionId) &&
              trace.Read(second).Events.All(e => e.SessionId == second.SessionId), "Cross-session observation leak.");
        Check(page.Events[0].RequestId == page.Events[1].RequestId &&
              page.Events[0].RequestId != page.Events[0].StreamId &&
              page.Events[0].RequestId != trace.Read(second).Events[0].RequestId,
            "Aliases must correlate only within one session and identifier domain.");
        Check(page.Events[0].ObservedOrigin == TraceOrigin.ChatGpt, "Official origin categorization failed.");
        Check(page.Events.All(e => e.Completeness == "UNKNOWN" && e.Outcome == TraceOutcome.Observed),
            "Adapter metadata claimed completeness or verified provider execution.");
        var serialized = JsonSerializer.Serialize(trace.Read(first), TraceNormalizer.JsonOptions);
        Check(!serialized.Contains(secret) && !serialized.Contains("https://") && !serialized.Contains("authorization="),
            "Raw URL/provider identifier leaked into serialized trace.");
        using (var json = JsonDocument.Parse(serialized))
        {
            Check(json.RootElement.GetProperty("schema_version").GetString() == "suprachat-trace/v1", "Trace schema drift.");
            Check(json.RootElement.GetProperty("events")[0].GetProperty("provider_surface").GetString() == "RESPONSES_SSE",
                "Trace enums must have stable string wire representation.");
        }

        using var other = new TraceNormalizer();
        var foreign = other.CreateSession();
        Refused(() => trace.Read(foreign));
        Refused(() => trace.SetCapture(foreign, TraceCaptureMode.Metadata));
        Refused(() => trace.TryObserve(foreign, observation));
        Refused(() => trace.CloseSession(foreign));
        other.SetCapture(foreign, TraceCaptureMode.Metadata);
        other.TryObserve(foreign, observation);
        Check(other.Read(foreign).Events[0].RequestId != page.Events[0].RequestId, "Host restart reused alias domain.");

        foreach (var invalid in new[]
        {
            observation with { ByteCount = -1 }, observation with { SourceSequence = -1 },
            observation with { LatencyMilliseconds = double.NaN }, observation with { LatencyMilliseconds = double.PositiveInfinity },
            observation with { Status = 600 }, observation with { EventKind = (TraceEventKind)999 },
            observation with { RequestId = new string('x', 513) }
        }) Check(!trace.TryObserve(first, invalid), "Invalid metadata accepted.");
        Check(trace.Read(first).CaptureGap, "Rejected observations silently disappeared.");
        trace.TryObserve(first, observation with { Origin = $"https://{secret}@chatgpt.com/" });
        Check(trace.Read(first).Events.Last().ObservedOrigin == TraceOrigin.Unknown && trace.Read(first).Events.Last().CaptureGap,
            "Credential URL accepted or pending gap lost.");
        trace.TryObserve(first, observation with { Origin = $"https://chatgpt.com.{secret}.example/path" });
        Check(trace.Read(first).Events.Last().ObservedOrigin == TraceOrigin.Unknown, "Spoofed official host accepted.");
        trace.SetCapture(first, TraceCaptureMode.Off);
        Check(!trace.TryObserve(first, observation), "Stop failed.");
        trace.SetCapture(first, TraceCaptureMode.Metadata);
        trace.TryObserve(first, observation with { EventKind = TraceEventKind.Reconnected });
        Check(trace.Read(first).Events.Last().CaptureGap, "Stop/resume interval was reported continuous.");

        // Flood one session: its ring cannot consume the other session's capacity.
        for (var i = 0; i < 30; i++) trace.TryObserve(first, observation);
        var overflow = trace.Read(first);
        Check(overflow.Events.Count == 8 && overflow.TotalEvictedEvents == 28 && overflow.CaptureGap &&
              overflow.EvictedThroughSequence > 0, "Ring overflow did not disclose exact cumulative loss.");
        Check(trace.Read(second).Events.Count == 2 && trace.Read(second).TotalEvictedEvents == 0,
            "One session's backpressure evicted another session's trace.");
        Check(!trace.Read(first, overflow.NextCursor).CaptureGap, "Tail cursor falsely reported old eviction as new loss.");
        Check(overflow.Events.Zip(overflow.Events.Skip(1)).All(pair =>
            pair.First.GlobalSequence < pair.Second.GlobalSequence &&
            pair.First.MonotonicOffsetMilliseconds <= pair.Second.MonotonicOffsetMilliseconds), "Trace ordering regressed.");

        // Source failures have safe fixed error text, including cancellation supplied by a source.
        var failed = false;
        try { await trace.PumpAsync(second, new FailureSource(secret)); }
        catch (InvalidOperationException ex)
        {
            failed = true;
            Check(!ex.ToString().Contains(secret) && ex.InnerException is null, "Source error leaked raw details.");
        }
        Check(failed && trace.Read(second).CaptureGap, "Crashed observer did not fail safely and disclose a gap.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { await trace.PumpAsync(second, new FailureSource(secret, cancel: true), cancellation.Token); }
        catch (OperationCanceledException ex)
        {
            cancelled = true;
            Check(!ex.ToString().Contains(secret), "Source cancellation leaked details.");
        }
        Check(cancelled, "Source cancellation was swallowed.");

        trace.CloseSession(first);
        Refused(() => trace.Read(first));
        Refused(() => trace.TryObserve(first, observation));
        var replacement = trace.CreateSession();
        Check(trace.Read(replacement).Events.Count == 0, "Closed-session data reached replacement.");
        trace.SetCapture(replacement, TraceCaptureMode.Metadata);
        await trace.PumpAsync(replacement, new FixtureSource());
        Check(trace.Read(replacement).Events.Select(e => e.EventKind).SequenceEqual(new[]
        {
            TraceEventKind.Request, TraceEventKind.Response, TraceEventKind.Chunk, TraceEventKind.Chunk,
            TraceEventKind.Reconnected, TraceEventKind.CaptureGap, TraceEventKind.Error, TraceEventKind.Cancelled
        }), "Synthetic source lifecycle changed or lost framing metadata.");
        Check(trace.Read(replacement).CaptureGap, "Explicit source gap disappeared.");

        using var concurrent = new TraceNormalizer(capacityPerSession: 128);
        var left = concurrent.CreateSession();
        var right = concurrent.CreateSession();
        concurrent.SetCapture(left, TraceCaptureMode.Metadata);
        concurrent.SetCapture(right, TraceCaptureMode.Metadata);
        await Task.WhenAll(Task.Run(() => Emit(concurrent, left)), Task.Run(() => Emit(concurrent, right)));
        var merged = concurrent.Read(left).Events.Concat(concurrent.Read(right).Events).OrderBy(e => e.GlobalSequence).ToArray();
        Check(merged.Length == 80 && merged.Select(e => e.GlobalSequence).SequenceEqual(Enumerable.Range(1, 80).Select(i => (long)i)),
            "Concurrent writers lost or duplicated host sequence numbers.");
        Check(merged.Select(e => e.EventId).Distinct().Count() == 80, "Event identity collision.");
        Console.WriteLine("Traffic trace T0 synthetic checks PASS (no live capture or actuation)");
    }

    private static void Emit(TraceNormalizer normalizer, LocalTraceSession session)
    {
        foreach (var surface in new[] { TraceSurface.Synthetic, TraceSurface.ResponsesSse, TraceSurface.ResponsesWebSocket, TraceSurface.CodexJsonRpc })
            for (var i = 0; i < 10; i++)
                normalizer.TryObserve(session, new TraceObservation(surface, TraceAdapter.Fixture,
                    TraceDirection.Ingress, i == 9 ? TraceEventKind.Completed : TraceEventKind.Chunk,
                    "http://127.0.0.1/fixture", RequestId: "synthetic-request", SourceSequence: i));
    }

    private sealed class FailureSource(string secret, bool cancel = false) : ITraceSource
    {
        public async IAsyncEnumerable<TraceObservation> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (cancel) throw new OperationCanceledException(secret, cancellationToken);
            if (secret.Length > 0) throw new InvalidOperationException(secret);
            yield break;
        }
    }

    private sealed class FixtureSource : ITraceSource
    {
        public async IAsyncEnumerable<TraceObservation> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var kinds = new[] { TraceEventKind.Request, TraceEventKind.Response, TraceEventKind.Chunk, TraceEventKind.Chunk,
                TraceEventKind.Reconnected, TraceEventKind.CaptureGap, TraceEventKind.Error, TraceEventKind.Cancelled };
            for (var i = 0; i < kinds.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new TraceObservation(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Ingress,
                    kinds[i], "http://127.0.0.1/fixture", RequestId: "fixture", StreamId: "stream", SourceSequence: i);
            }
        }
    }
}
