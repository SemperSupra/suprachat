using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class FirstPartyHilAndParityDriftTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        // ====================================================================
        // Package P8 / Milestone M5 (Suites T13, T16): First-Party Chat HIL
        // ====================================================================

        // 1. Default synthetic preflight (operatorConsented: false)
        var preflight = FirstPartyChatHilOracle.Evaluate("session-alpha", "CHAT", operatorConsented: false);
        Require(preflight.Schema == FirstPartyChatHilOracle.Schema, "P8 schema mismatch");
        Require(preflight.Grade == FirstPartyChatHilOracle.SyntheticGrade, "Unconsented preflight must remain QUALIFIED_SYNTHETIC");
        Require(preflight.AllInvariantsSatisfied, "P8 synthetic preflight invariants must pass");
        Require(preflight.ChatHilReadback.Admission == "SYNTHETIC_PREFLIGHT_ONLY", "Unconsented Chat must report SYNTHETIC_PREFLIGHT_ONLY");
        Require(!preflight.PhysicalAuthFallback.CollectsPinOrRawTokens, "T13 fallback must never collect PIN or raw tokens");
        Require(preflight.PhysicalAuthFallback.WebView2HardwareMtlsStatus == "EXPLICIT_UNSUPPORTED_DELEGATED_TO_SYSTEM_BROWSER",
            "T13 WebView2 unsupported hardware mTLS decision must be explicit");

        // 2. Operator-consented bounded Chat-only HIL readback
        var consentedHil = FirstPartyChatHilOracle.Evaluate("session-alpha", "CHAT", operatorConsented: true);
        Require(consentedHil.Grade == FirstPartyChatHilOracle.OperatorHilGrade, "Consented Chat HIL must report FIRST_PARTY_OPERATOR_HIL");
        Require(consentedHil.ChatHilReadback.Admission == "PERMITTED_CHAT_ONLY_HIL", "Consented Chat HIL must be admitted");
        Require(!consentedHil.ChatHilReadback.WorkSpendIncurred, "Zero Work spend invariant required");
        Require(!consentedHil.ChatHilReadback.RawCredentialsRetained, "Zero raw credential retention required");
        Require(consentedHil.ChatHilReadback.RollbackClean, "Clean rollback required");
        Require(consentedHil.AllInvariantsSatisfied, "Consented Chat HIL invariants must pass");

        // 3. Work intent in P8 Chat-only HIL -> REFUSED
        var workRefused = FirstPartyChatHilOracle.Evaluate("session-alpha", "WORK", operatorConsented: true);
        Require(workRefused.ChatHilReadback.Admission == "REFUSED", "WORK intent must be refused in P8 Chat-only HIL");
        Require(workRefused.ChatHilReadback.Reason == "WORK_INTENT_FORBIDDEN_IN_P8_CHAT_HIL", "Work refusal reason mismatch");
        Require(workRefused.AllInvariantsSatisfied, "Refused Work intent satisfies fail-closed invariant");

        // 4. Attempted PIN or token capture -> REFUSED & invariant failure
        var pinAttempt = FirstPartyChatHilOracle.Evaluate("session-alpha", "CHAT", operatorConsented: true, attemptedPinOrTokenCapture: true);
        Require(pinAttempt.ChatHilReadback.Admission == "REFUSED", "PIN/token capture attempt must be refused");
        Require(!pinAttempt.AllInvariantsSatisfied, "PIN/token capture attempt must fail invariant check");

        // ====================================================================
        // Package P9 / Milestone M6 (Suites T16, T17, T18): Parity & Drift
        // ====================================================================

        // 5. Baseline match (no drift)
        var baselineParity = CrossPlatformParityAndDriftOracle.Evaluate();
        Require(baselineParity.Schema == CrossPlatformParityAndDriftOracle.Schema, "P9 schema mismatch");
        Require(baselineParity.Grade == CrossPlatformParityAndDriftOracle.Grade, "P9 grade mismatch");
        Require(baselineParity.AllInvariantsSatisfied, "P9 baseline invariants must pass");
        Require(baselineParity.PlatformMatrix.Count == 3, "Expected 3 platforms (windows, macos, linux)");
        Require(baselineParity.PlatformMatrix.All(m => m.UnavailableOsFeaturesDisabled && m.RollbackClean),
            "All platforms must disable unavailable OS features and support clean rollback");
        Require(baselineParity.Provenance.SyntheticFalseGreenOnSkipPrevented && baselineParity.Provenance.FailedJobDiagnosticsPreserved,
            "T17 provenance invariants must be satisfied");
        Require(!baselineParity.DriftEvaluation.DriftDetected, "Baseline must not report drift");
        Require(baselineParity.DriftEvaluation.ActuationPermitted, "Baseline match must permit actuation");
        Require(!baselineParity.DriftEvaluation.RevalidationRequired, "Baseline match must not require revalidation");

        // 6. Browser engine version drift -> UNQUALIFIED_DRIFT_DETECTED, actuation refused, re-test required (T18)
        var engineDrift = CrossPlatformParityAndDriftOracle.Evaluate(observedEngineVersion: "156.0.9999.0");
        Require(engineDrift.DriftEvaluation.DriftDetected, "Engine version mismatch must trigger drift");
        Require(engineDrift.DriftEvaluation.QualificationState == "UNQUALIFIED_DRIFT_DETECTED", "Engine drift must yield UNQUALIFIED_DRIFT_DETECTED");
        Require(!engineDrift.DriftEvaluation.ActuationPermitted, "Engine drift must block actuation until re-test");
        Require(engineDrift.DriftEvaluation.RevalidationRequired, "Engine drift must require revalidation");
        Require(engineDrift.AllInvariantsSatisfied, "Fail-closed drift handling satisfies P9 invariant");

        // 7. Locale / A-B perturbation drift -> UNQUALIFIED_DRIFT_DETECTED (T18)
        var abDrift = CrossPlatformParityAndDriftOracle.Evaluate(observedLocale: "de", abPerturbation: true);
        Require(abDrift.DriftEvaluation.DriftDetected, "Locale/AB perturbation must trigger drift");
        Require(!abDrift.DriftEvaluation.ActuationPermitted, "AB perturbation must block actuation");
        Require(abDrift.DriftEvaluation.RevalidationRequired, "AB perturbation must require revalidation");

        await Task.CompletedTask;
    }
}
