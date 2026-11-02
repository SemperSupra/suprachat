using SupraChat.ProviderContracts;

var total = 0;
void Assert(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    total++;
}

const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
ProviderBinding Binding(string session, string account, string provider, string id) =>
    new(ProviderAdmission.BindingSchema, id, session, provider,
        ProviderSurface.Api, account, "model-v1", "local-only",
        "cap-v1", 1, 1, 1, "test-adapter/v1");
ProviderCapabilities Caps(string provider, CapabilityState chat = CapabilityState.Supported) =>
    new(ProviderAdmission.CapabilitiesSchema, provider, "cap-v1",
        new Dictionary<string, CapabilityState>(StringComparer.Ordinal)
        { ["chat"] = chat, ["tools"] = CapabilityState.Unsupported });
ProviderOperation Operation(ProviderBinding b, string id) =>
    new(ProviderAdmission.OperationSchema, id, b.SessionId, b.BindingId,
        b.ProviderId, b.AccountRef, b.ModelRef, b.EndpointPolicyRef,
        b.CapabilityRevision, b.AuthGeneration, b.SessionEpoch,
        b.BindingRevision, b.AdapterVersion, "chat", digest);

var a = Binding("session-1", "account-1", "mock-a", "binding-1");
var b = Binding("session-2", "account-2", "mock-b", "binding-2");
var opA = Operation(a, "op-1");
var opB = Operation(b, "op-2");
Assert(ProviderAdmission.Evaluate(a, Caps("mock-a"), opA).Admitted, "mock A admitted");
Assert(ProviderAdmission.Evaluate(b, Caps("mock-b"), opB).Admitted, "mock B admitted");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opB).Admitted, "cross-session refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { AccountRef = "account-2" }).Admitted, "cross-account refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { AuthGeneration = 2 }).Admitted, "stale auth refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { SessionEpoch = 2 }).Admitted, "stale epoch refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { BindingRevision = 2 }).Admitted, "stale binding refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { ModelRef = "other" }).Admitted, "model switch refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { EndpointPolicyRef = "cloud" }).Admitted, "endpoint switch refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { AdapterVersion = "changed" }).Admitted, "adapter version switch refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a", CapabilityState.Unknown), opA).Admitted, "unknown capability refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a", CapabilityState.Unsupported), opA).Admitted, "unsupported capability refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { RequiredCapability = "tools" }).Admitted, "unsupported tools refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a"), opA with { RequestDigest = "bad" }).Admitted, "invalid digest refused");
Assert(!ProviderAdmission.Evaluate(a with { Schema = "future" }, Caps("mock-a"), opA).Admitted, "unknown schema refused");
Assert(!ProviderAdmission.Evaluate(a with { AuthGeneration = 0 }, Caps("mock-a"), opA).Admitted, "zero auth generation refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-b"), opA).Admitted, "capabilities provider mismatch refused");
Assert(!ProviderAdmission.Evaluate(a, Caps("mock-a") with { Revision = "old" }, opA).Admitted, "stale capability revision refused");
Assert(ProviderAdmission.CompareReplay(opA, opA with { }) == "IDENTICAL_REPLAY", "identical replay recognized");
Assert(ProviderAdmission.CompareReplay(opA, opA with { RequestDigest = new string('b', 64) }) == "REPLAY_CONFLICT", "changed payload refused");
Assert(ProviderAdmission.CompareReplay(opA, opB) == "DISTINCT_OPERATION", "distinct operations");
var events = new[]
{
    new ProviderEvent("op-1", 0, ProviderEventKind.Delta),
    new ProviderEvent("op-1", 1, ProviderEventKind.Completed)
};
Assert(ProviderStreamReconciler.Classify("op-1", events, true) == ProviderOutcome.Completed, "terminal completed");
Assert(ProviderStreamReconciler.Classify("op-1", events, false) == ProviderOutcome.Unknown, "unclosed stream not completed");
Assert(ProviderStreamReconciler.Classify("op-1", events[..1], true) == ProviderOutcome.Unknown, "missing terminal unknown");
Assert(ProviderStreamReconciler.Classify("op-1", new[] { events[0], events[0] }, true) == ProviderOutcome.Unknown, "duplicate sequence unknown");
Assert(ProviderStreamReconciler.Classify("op-2", events, true) == ProviderOutcome.Unknown, "cross-operation stream unknown");
Assert(ProviderStreamReconciler.Classify("op-1", new[] { events[1], events[0] }, true) == ProviderOutcome.Unknown, "late delta unknown");
Assert(ProviderStreamReconciler.Classify("op-1", new[] { new ProviderEvent("op-1", 0, ProviderEventKind.Incomplete) }, true) == ProviderOutcome.Incomplete, "incomplete mapped");
Assert(ProviderStreamReconciler.Classify("op-1", new[] { new ProviderEvent("op-1", 0, ProviderEventKind.Failed) }, true) == ProviderOutcome.Failed, "failure mapped");
Assert(ProviderUsageValidation.Valid(new(UsageSource.Unknown, null, null, null, null)), "unknown usage valid");
Assert(!ProviderUsageValidation.Valid(new(UsageSource.Unknown, 0, null, null, null)), "unknown not fabricated zero");
Assert(!ProviderUsageValidation.Valid(new(UsageSource.ProviderReported, -1, 3, null, null)), "negative usage refused");
Assert(!ProviderUsageValidation.Valid(new(UsageSource.ProviderReported, 5, 3, 0.1m, null)), "cost without currency refused");
Assert(ProviderUsageValidation.Valid(new(UsageSource.ProviderReported, 5, 3, 0.1m, "USD")), "source-tagged usage");
var goodReceipt = new ProviderReceipt(
    ProviderReceiptPolicy.Schema, "op-1", "binding-1", ProviderOutcome.Completed,
    null, UsageSource.ProviderReported, 5, 3, 40, 12, "req-abc", false);
Assert(ProviderReceiptPolicy.Valid(goodReceipt), "metadata-only success receipt valid");
Assert(!ProviderReceiptPolicy.Valid(goodReceipt with { ContainsContent = true }), "payload-bearing receipt refused");
Assert(!ProviderReceiptPolicy.Valid(goodReceipt with { ProviderRequestAlias = "Bearer secret" }), "unbounded request alias refused");
Assert(!ProviderReceiptPolicy.Valid(goodReceipt with { FirstChunkMs = 41 }), "first chunk after completion refused");
Assert(!ProviderReceiptPolicy.Valid(goodReceipt with { Outcome = ProviderOutcome.Admitted }), "admission not settled");
Assert(!ProviderReceiptPolicy.Valid(goodReceipt with { FailureClass = ProviderFailureClass.Policy }), "success cannot claim failure");
var unknownReceipt = goodReceipt with {
    Outcome = ProviderOutcome.Unknown,
    FailureClass = ProviderFailureClass.Unknown,
    UsageSource = UsageSource.Unknown,
    InputTokens = null,
    OutputTokens = null
};
Assert(ProviderReceiptPolicy.Valid(unknownReceipt), "uncertain outcome represented explicitly");
Assert(!ProviderReceiptPolicy.Valid(unknownReceipt with { InputTokens = 0 }), "unknown usage not fabricated");
var retry = new ProviderRetryFacts(true, false, false, SideEffectCertainty.None);
Assert(ProviderRetryPolicy.Eligible(retry), "transient pre-submit candidate eligible");
Assert(!ProviderRetryPolicy.Eligible(retry with { Submitted = true }), "submitted request never auto-retried");
Assert(!ProviderRetryPolicy.Eligible(retry with { StreamingStarted = true }), "streaming request never auto-retried");
Assert(!ProviderRetryPolicy.Eligible(retry with { SideEffects = SideEffectCertainty.Possible }), "possible side effect blocks retry");
Assert(!ProviderRetryPolicy.Eligible(retry with { Transient = false }), "permanent failure blocks retry");
Console.WriteLine($"QUALIFIED_SYNTHETIC_CONTRACT_ONLY cases={total} providers=2 session_authority_changed=false live_network=false");
