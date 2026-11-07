using System.Text.Json;
using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class MultiVantageDifferentialTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        // 1. Synthetic baseline differential evaluation across paired sessions
        {
            var report = MultiVantageDifferentialOracle.EvaluateSyntheticBaseline(
                sessionAId: "session-alpha-chat",
                sessionBId: "session-beta-work",
                intentA: "CHAT",
                intentB: "WORK",
                leaseConfirmedB: false);

            Require(report.Schema == MultiVantageDifferentialOracle.Schema, "Schema mismatch");
            Require(report.Grade == MultiVantageDifferentialOracle.Grade, "Grade mismatch");
            Require(report.SessionsIsolated, "Paired sessions must be isolated");
            Require(report.SecurityInvariantsPassed, "Security invariants must pass");
            Require(report.ParityPassed, "Parity must pass");
            Require(report.Violations.Count == 0, "No violations expected in valid baseline");
            Require(report.Vantages.Count == 4, "Expected 4 evaluated vantages");

            // Verify Session Isolation vantage
            var isolationVantage = report.Vantages.First(v => v.VantageName == "SessionIsolation");
            Require(isolationVantage.Pass, "Session isolation vantage must pass");

            // Verify Experience Mode Policy vantage
            var modeVantage = report.Vantages.First(v => v.VantageName == "ExperienceModePolicy");
            Require(modeVantage.Pass, "Mode policy vantage must pass");
            Require((bool)(modeVantage.Details["decision_a_allowed"] ?? false) == true, "Session A (CHAT) must be allowed");
            Require((bool)(modeVantage.Details["decision_b_allowed"] ?? true) == false, "Session B (unleased WORK) must be refused");
            Require((string)(modeVantage.Details["decision_b_admission"] ?? "") == "REFUSED", "Session B admission must be REFUSED");
            Require((string)(modeVantage.Details["decision_b_reason"] ?? "") == "WORK_APPROVAL_REQUIRED", "Session B reason must be WORK_APPROVAL_REQUIRED");

            // Verify Multi-Audience and Accessibility vantage
            var audienceVantage = report.Vantages.First(v => v.VantageName == "MultiAudienceAndAccessibility");
            Require(audienceVantage.Pass, "Audience & accessibility vantage must pass");
        }

        // 2. Cross-session HMAC isolation and zero-sentinel leakage (T04 / T08)
        {
            var oracleA = new SyntheticTraceShapeOracle();
            var handleA = oracleA.Session();
            oracleA.Enable(handleA, true);

            var oracleB = new SyntheticTraceShapeOracle();
            var handleB = oracleB.Session();
            oracleB.Enable(handleB, true);

            const string sentinelToken = "SENTINEL_SECRET_TOKEN_DO_NOT_LEAK";
            var payload = JsonSerializer.Serialize(new
            {
                requestId = "identical-request-id-across-sessions",
                request = new { url = $"https://chatgpt.com/api?secret={sentinelToken}" }
            });

            oracleA.Ingest(handleA, "requestWillBeSent", payload);
            oracleB.Ingest(handleB, "requestWillBeSent", payload);

            var snapA = oracleA.Snapshot(handleA);
            var snapB = oracleB.Snapshot(handleB);

            var serializedA = JsonSerializer.Serialize(snapA);
            var serializedB = JsonSerializer.Serialize(snapB);

            Require(!serializedA.Contains(sentinelToken, StringComparison.Ordinal), "Sentinel leaked in Session A trace");
            Require(!serializedB.Contains(sentinelToken, StringComparison.Ordinal), "Sentinel leaked in Session B trace");
            Require(snapA.Events[0].Correlation != snapB.Events[0].Correlation, "Different sessions must produce distinct HMAC correlation aliases");

            var inputA = new MultiVantageSessionInput("session-001", "CHAT", "en", 1, true, snapA.Events);
            var inputB = new MultiVantageSessionInput("session-002", "CHAT", "de", 1, true, snapB.Events);

            var report = MultiVantageDifferentialOracle.Evaluate(inputA, inputB);
            Require(report.SessionsIsolated, "Distinct sessions with separate HMACs must be isolated");
            Require(report.SecurityInvariantsPassed, "Security invariants must pass");
        }

        // 3. Negative controls: detection of cross-session correlation leaks & duplicate session IDs
        {
            // Case A: Identical Session IDs
            var inputSame = new MultiVantageSessionInput("session-duplicate", "CHAT");
            var reportSame = MultiVantageDifferentialOracle.Evaluate(inputSame, inputSame);
            Require(!reportSame.SessionsIsolated, "Identical session IDs must fail isolation");
            Require(reportSame.Violations.Any(v => v.StartsWith("SESSIONS_IDENTICAL", StringComparison.Ordinal)), "SESSIONS_IDENTICAL violation expected");

            // Case B: Cross-session HMAC correlation collision injection
            var collidingEvent = new TraceShapeEvent("requestWillBeSent", "0123456789abcdef0123", 100, 200, "SYNTHETIC");
            var inputLeakA = new MultiVantageSessionInput("session-leak-a", "CHAT", TraceEvents: new[] { collidingEvent });
            var inputLeakB = new MultiVantageSessionInput("session-leak-b", "CHAT", TraceEvents: new[] { collidingEvent });

            var reportLeak = MultiVantageDifferentialOracle.Evaluate(inputLeakA, inputLeakB);
            Require(!reportLeak.SessionsIsolated, "Shared correlation alias must fail isolation");
            Require(reportLeak.Violations.Any(v => v.StartsWith("CROSS_SESSION_CORRELATION_LEAK", StringComparison.Ordinal)), "CROSS_SESSION_CORRELATION_LEAK violation expected");
        }

        // 4. Multi-lingual perturbation differential across German and Thai (T05 / T12)
        {
            var deControls = new[]
            {
                new BrowserExperienceModePolicy.ControlElement("switch", "tab", "Chat", true, true),
                new BrowserExperienceModePolicy.ControlElement("switch", "tab", "Arbeit", true, false)
            };

            var thControls = new[]
            {
                new BrowserExperienceModePolicy.ControlElement("switch", "tab", "\u0e41\u0e0a\u0e17", true, true),
                new BrowserExperienceModePolicy.ControlElement("switch", "tab", "\u0e07\u0e32\u0e19", true, false)
            };

            var inputDe = new MultiVantageSessionInput("session-de", "CHAT", "de", DomControls: deControls);
            var inputTh = new MultiVantageSessionInput("session-th", "CHAT", "th", DomControls: thControls);

            var reportLang = MultiVantageDifferentialOracle.Evaluate(inputDe, inputTh);
            Require(reportLang.SessionsIsolated, "Multi-lingual sessions must be isolated");
            Require(reportLang.SecurityInvariantsPassed, "Multi-lingual security invariants must pass");
            Require(reportLang.ParityPassed, "Multi-lingual parity must pass");
        }

        // 5. Multi-Audience projection invariance across 3 audiences (T15)
        {
            var inputA = new MultiVantageSessionInput("session-aud-a", "CHAT", "en", 1, true);
            var inputB = new MultiVantageSessionInput("session-aud-b", "WORK", "en", 1, true); // Leased work

            var report = MultiVantageDifferentialOracle.Evaluate(inputA, inputB);
            Require(report.ParityPassed, "Parity across audiences must pass");
            Require(report.SecurityInvariantsPassed, "Leased WORK and CHAT must both be allowed");

            var modeVantage = report.Vantages.First(v => v.VantageName == "ExperienceModePolicy");
            Require((bool)(modeVantage.Details["decision_a_allowed"] ?? false) == true, "Session A CHAT allowed");
            Require((bool)(modeVantage.Details["decision_b_allowed"] ?? false) == true, "Session B leased WORK allowed");
        }

        await Task.CompletedTask;
    }
}
