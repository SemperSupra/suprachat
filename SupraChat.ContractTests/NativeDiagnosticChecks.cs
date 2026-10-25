using System.Text.Json;
using SupraChat.Core;

internal static class NativeDiagnosticChecks
{
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Native diagnostic qualification failed."); }
    internal static void Run()
    {
        Require(NativeCaptureDiagnostics.InactiveReason("EXPIRED") == NativeCaptureEndReason.LeaseExpired);
        Require(NativeCaptureDiagnostics.InactiveReason("RECORDER_STOPPED") == NativeCaptureEndReason.RecorderStopped);
        Require(NativeCaptureDiagnostics.InactiveReason("RECORDER_UNAVAILABLE") == NativeCaptureEndReason.RecorderUnavailable);
        Require(NativeCaptureDiagnostics.InactiveReason("STOPPED") == NativeCaptureEndReason.InactiveUnknown);
        using var trace = new TraceNormalizer(); var session = trace.CreateSession();
        trace.SetCapture(session, TraceCaptureMode.Metadata);
        var diagnostics = new NativeCaptureDiagnostics();
        var parser = new NativeMetadataEventProcessor(new("https://chatgpt.com/"), TraceSurface.Synthetic, trace, session, diagnostics, () => { });
        parser.Process("requestWillBeSent", "{\"requestId\":\"private-diagnostic-sentinel\",\"request\":{\"url\":\"https://chatgpt.com/path?private=value\"}}");
        parser.Process("loadingFinished", "{\"requestId\":\"private-diagnostic-sentinel\"}");
        Require(parser.ObservedNetworkEvents == 2 && diagnostics.Snapshot().TotalIncidents == 0);
        trace.SetCapture(session, TraceCaptureMode.Off);
        Require(trace.Read(session).CaptureGap && diagnostics.Snapshot().TotalIncidents == 0); // Intentional stop != a dropped observation.
        trace.SetCapture(session, TraceCaptureMode.Metadata);
        parser.Process("dataReceived", new string('x', 1024 * 1024 + 1));
        parser.Process("dataReceived", "{}");
        parser.Process("dataReceived", "{");
        parser.Process("requestWillBeSent", "{\"requestId\":\"redirect\",\"request\":{\"url\":\"https://chatgpt.com/\"}}");
        parser.Process("requestWillBeSent", "{\"requestId\":\"redirect\",\"request\":{\"url\":\"https://foreign.invalid/\"}}");
        var snapshot = diagnostics.Snapshot();
        Require(snapshot.ObservationIncidents == 4 && snapshot.LifecycleIncidents == 0 && snapshot.Reasons.Count == 4);
        foreach (var key in new[] { "RawEventOversize", "MissingRequestId", "MetadataMalformed", "RequestScopeRevoked" }) Require(snapshot.Reasons[key] == 1);
        diagnostics.Record(NativeDiagnosticReason.DisableFailure); diagnostics.Record(NativeDiagnosticReason.CleanupTimeout);
        snapshot = diagnostics.Snapshot(); Require(snapshot.ObservationIncidents == 4 && snapshot.LifecycleIncidents == 2);
        var combined = NativeCaptureDiagnostics.Merge(snapshot, snapshot);
        Require(combined.TotalIncidents == 12 && combined.Reasons["DisableFailure"] == 2);
        var status = new NativeMetadataStatus(new(session.SessionId, 0, "STOPPED", false, 0, 0), false, false,
            parser.ObservedNetworkEvents, snapshot.TotalIncidents, "SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN",
            Diagnostics: snapshot, EndReason: NativeCaptureEndReason.SummarySave, CleanupComplete: true, EnableCompleted: true, DisableCompleted: true);
        trace.SetCapture(session, TraceCaptureMode.Off);
        var summary = NativeMetadataSummary.Create(status, trace.Read(session), "synthetic-hash");
        Require(summary.GetProperty("schema").GetString()!.EndsWith("/v2") && summary.GetProperty("stopped_interval_disclosed").GetBoolean());
        Require(summary.GetProperty("first_lease_end_reason").GetString() == "SummarySave" && !summary.GetProperty("capture_active").GetBoolean());
        var json = summary.GetRawText();
        Require(!json.Contains("private-diagnostic-sentinel") && !json.Contains("private=value") && !json.Contains("foreign.invalid"));
        Console.WriteLine("Native diagnostic checks PASS (synthetic parser incidents, intentional stop, lifecycle distinction, summary privacy)");
    }
}
