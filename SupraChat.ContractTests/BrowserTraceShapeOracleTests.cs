using System.Text.Json;
using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class BrowserTraceShapeOracleTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        // 1. Default OFF, metadata-only lifecycle, and no sentinel leakage
        {
            var oracle = new SyntheticTraceShapeOracle();
            var session = oracle.Session();

            const string reqId = "synthetic-request-id-001";
            const string token = "SYNTHETIC_SENTINEL_TOKEN_SECRET";
            var reqPayload = JsonSerializer.Serialize(new
            {
                requestId = reqId,
                request = new { url = $"https://chatgpt.com/resource?x={token}", postData = token }
            });
            var respPayload = JsonSerializer.Serialize(new
            {
                requestId = reqId,
                response = new { url = "https://chatgpt.com/resource", status = 200, body = token },
                dataLength = 42L
            });

            Require(oracle.Ingest(session, "requestWillBeSent", reqPayload) == TraceIngestOutcome.Off, "Default state must be OFF");
            var offSnapshot = oracle.Snapshot(session);
            Require(!offSnapshot.Enabled, "Snapshot must report Enabled=false when OFF");
            Require(offSnapshot.Events.Count == 0, "No events retained when OFF");
            Require(offSnapshot.Gaps == 0, "No gaps reported initially");

            oracle.Enable(session, true);
            var outcome1 = oracle.Ingest(session, "requestWillBeSent", reqPayload);
            Require(outcome1 == TraceIngestOutcome.Observed, "requestWillBeSent must be Observed");

            var outcome2 = oracle.Ingest(session, "responseReceived", respPayload);
            Require(outcome2 == TraceIngestOutcome.Observed, "responseReceived must be Observed");

            var outcome3 = oracle.Ingest(session, "dataReceived", respPayload);
            Require(outcome3 == TraceIngestOutcome.Observed, "dataReceived must be Observed");

            var outcome4 = oracle.Ingest(session, "eventSourceMessageReceived", respPayload);
            Require(outcome4 == TraceIngestOutcome.Observed, "eventSourceMessageReceived must be Observed");

            var outcome5 = oracle.Ingest(session, "webSocketFrameReceived", respPayload);
            Require(outcome5 == TraceIngestOutcome.Observed, "webSocketFrameReceived must be Observed");

            var snapshot = oracle.Snapshot(session);
            var serialized = JsonSerializer.Serialize(snapshot);

            Require(!serialized.Contains(token, StringComparison.Ordinal), "Sentinel token leaked in trace shape snapshot");
            Require(!serialized.Contains(reqId, StringComparison.Ordinal), "Raw requestId leaked in trace shape snapshot");
            Require(!serialized.Contains("/resource", StringComparison.Ordinal), "URL path leaked in trace shape snapshot");
            Require(snapshot.Events.Count == 5, "Expected 5 observed events");
            Require(snapshot.Events[2].Bytes == 42L, "dataReceived bytes mismatch");
            Require(snapshot.Events[1].Status == 200, "responseReceived status mismatch");
            Require(snapshot.Completeness == "UNKNOWN", "Completeness must be UNKNOWN");
            Require(snapshot.Grade == "SYNTHETIC_ONLY", "Grade must be SYNTHETIC_ONLY");
            Require(snapshot.Events[0].Correlation?.Length == 20, "HMAC correlation alias must be 20 hex characters");
        }

        // 2. Per-session HMAC alias isolation
        {
            var oracle = new SyntheticTraceShapeOracle();
            var a = oracle.Session();
            var b = oracle.Session();
            oracle.Enable(a, true);
            oracle.Enable(b, true);

            var reqPayload = JsonSerializer.Serialize(new
            {
                requestId = "shared-request-id",
                request = new { url = "https://chatgpt.com/api" }
            });

            oracle.Ingest(a, "requestWillBeSent", reqPayload);
            oracle.Ingest(b, "requestWillBeSent", reqPayload);

            var snapA = oracle.Snapshot(a);
            var snapB = oracle.Snapshot(b);

            Require(snapA.Events[0].Correlation != snapB.Events[0].Correlation,
                "Different sessions must produce isolated HMAC correlation aliases for identical request IDs");
        }

        // 3. Negative controls, malformed, oversized, and spoofed origin
        {
            var oracle = new SyntheticTraceShapeOracle("https://chatgpt.com", limit: 3);
            var s = oracle.Session();
            oracle.Enable(s, true);

            // Unknown event name -> GAP
            Require(oracle.Ingest(s, "unknownEvent", "{}") == TraceIngestOutcome.Gap, "Unknown event must produce GAP");

            // Malformed JSON -> GAP
            Require(oracle.Ingest(s, "responseReceived", "{not valid json") == TraceIngestOutcome.Gap, "Malformed JSON must produce GAP");

            // Oversized payload (>64KB) -> GAP
            var oversized = new string('x', 70000);
            Require(oracle.Ingest(s, "requestWillBeSent", oversized) == TraceIngestOutcome.Gap, "Oversized payload must produce GAP");

            // Off-origin spoofed request -> IGNORED
            var spoofedReq = JsonSerializer.Serialize(new
            {
                requestId = "spoofed-001",
                request = new { url = "https://chatgpt.com.evil.invalid/steal" }
            });
            Require(oracle.Ingest(s, "requestWillBeSent", spoofedReq) == TraceIngestOutcome.Ignored, "Off-origin request must be IGNORED");

            // Un-correlated data received -> IGNORED
            var uncorrData = JsonSerializer.Serialize(new
            {
                requestId = "uncorrelated-002",
                dataLength = 100L
            });
            Require(oracle.Ingest(s, "dataReceived", uncorrData) == TraceIngestOutcome.Ignored, "Uncorrelated dataReceived must be IGNORED");

            // Valid request then negative dataLength -> GAP
            var validReq = JsonSerializer.Serialize(new
            {
                requestId = "req-valid-003",
                request = new { url = "https://chatgpt.com/good" }
            });
            Require(oracle.Ingest(s, "requestWillBeSent", validReq) == TraceIngestOutcome.Observed, "Valid request must be OBSERVED");

            var negativeData = JsonSerializer.Serialize(new
            {
                requestId = "req-valid-003",
                dataLength = -5L
            });
            Require(oracle.Ingest(s, "dataReceived", negativeData) == TraceIngestOutcome.Gap, "Negative dataLength must produce GAP");

            var snap = oracle.Snapshot(s);
            Require(snap.Gaps >= 4, "Gaps count must account for all gap incidents");
            Require(snap.Events.Count <= 3, "Events count must not exceed limit 3");

            // Disable clears active and adds gap
            oracle.Enable(s, false);
            Require(oracle.Ingest(s, "dataReceived", uncorrData) == TraceIngestOutcome.Off, "Disabled session must report OFF");
        }

        // 4. Bounded capacity stress (512 synthetic events with limit 8)
        {
            var oracle = new SyntheticTraceShapeOracle("https://chatgpt.com", limit: 8);
            var s = oracle.Session();
            oracle.Enable(s, true);

            for (int i = 0; i < 512; i++)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    requestId = $"sensitive-key-{i}",
                    request = new { url = $"https://chatgpt.com/item?private={i}" }
                });
                oracle.Ingest(s, "requestWillBeSent", payload);
            }

            var snap = oracle.Snapshot(s);
            var serialized = JsonSerializer.Serialize(snap);

            Require(snap.Events.Count <= 8, "Bounded capacity exceeded (must be <= 8)");
            Require(snap.Gaps > 0, "Evicted events must increment gaps counter");
            Require(!serialized.Contains("sensitive-key-", StringComparison.Ordinal), "Sensitive request IDs leaked during stress test");
            Require(!serialized.Contains("/item?private=", StringComparison.Ordinal), "Private URL leaked during stress test");
            Require(snap.Completeness == "UNKNOWN", "Completeness must remain UNKNOWN");
        }

        // 5. Session capacity limits, revocation, and stale handle refusal
        {
            var oracle = new SyntheticTraceShapeOracle();
            var s1 = oracle.Session();
            var s2 = oracle.Session();
            var s3 = oracle.Session();
            var s4 = oracle.Session();

            bool fifthRefused = false;
            try
            {
                oracle.Session();
            }
            catch (InvalidOperationException)
            {
                fifthRefused = true;
            }
            Require(fifthRefused, "Fifth session allocation must be refused (max 4)");

            oracle.Enable(s1, true);
            oracle.Revoke(s1);

            bool staleRefused = false;
            try
            {
                oracle.Snapshot(s1);
            }
            catch (ArgumentException)
            {
                staleRefused = true;
            }
            Require(staleRefused, "Snapshot on revoked session handle must be refused");

            var s5 = oracle.Session();
            oracle.Enable(s5, true);
            var validReq = JsonSerializer.Serialize(new
            {
                requestId = "s5-req",
                request = new { url = "https://chatgpt.com/test" }
            });
            Require(oracle.Ingest(s5, "requestWillBeSent", validReq) == TraceIngestOutcome.Observed, "Allocated session after revocation must work");
            oracle.Close(s5);

            bool closedRefused = false;
            try
            {
                oracle.Ingest(s5, "requestWillBeSent", validReq);
            }
            catch (ArgumentException)
            {
                closedRefused = true;
            }
            Require(closedRefused, "Ingest on closed session handle must be refused");
        }

        // 6. NetworkMetadataSensor wiring check
        {
            var sensor = new NetworkMetadataSensor();
            Require(sensor.Descriptor.SensorId == "network-metadata", "SensorId mismatch");
            Require(sensor.Descriptor.ObservationKind == "NETWORK_SHAPE_METADATA", "ObservationKind mismatch");

            var observation = await sensor.ObserveAsync();
            Require(observation.Schema == BrowserCapabilityContract.ObservationSchema, "Observation schema mismatch");
            Require(observation.Completeness == "UNKNOWN", "Completeness must report UNKNOWN");
            Require((bool)(observation.Data["contains_authorization_header"] ?? true) == false, "Must not contain auth header");
            Require((bool)(observation.Data["contains_cookie_header"] ?? true) == false, "Must not contain cookie header");
            Require((bool)(observation.Data["contains_body_payload"] ?? true) == false, "Must not contain body payload");
            Require((string)(observation.Data["trace_shape_schema"] ?? "") == SyntheticTraceShapeOracle.Schema, "Trace shape schema mismatch");
        }

        await Task.CompletedTask;
    }
}
