using System.Net;
using System.Net.Http;
using SupraChat.ProviderContracts;
using SupraChat.ProviderMockTransport;

var count = 0;
void Check(bool test, string label)
{
    if (!test) throw new InvalidOperationException("FAIL: " + label);
    count++;
}
const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
ProviderBinding Binding(string p, string session) =>
    new(ProviderAdmission.BindingSchema, "b-" + p, session, p, ProviderSurface.Api,
        "acct-alias", "model-test", "synthetic-only", "caps-v1", 1, 1, 1, "adapter-v1");
ProviderCapabilities Caps(string p) =>
    new(ProviderAdmission.CapabilitiesSchema, p, "caps-v1",
        new Dictionary<string, CapabilityState> { ["chat"] = CapabilityState.Supported });
ProviderOperation Op(ProviderBinding b) =>
    new(ProviderAdmission.OperationSchema, "op-" + b.ProviderId, b.SessionId, b.BindingId,
        b.ProviderId, b.AccountRef, b.ModelRef, b.EndpointPolicyRef, b.CapabilityRevision,
        b.AuthGeneration, b.SessionEpoch, b.BindingRevision, b.AdapterVersion,
        "chat", digest);
string A(params string[] messages) => string.Join("", messages.Select((m, i) =>
    "data: {\"operation\":\"op-mock-a\",\"index\":" + i +
    ",\"type\":\"" + m + "\",\"text\":\"SECRET_OUTPUT_CANARY\"}\n\n"));
string B(params string[] phases) => string.Join("", phases.Select((m, i) =>
    "data: {\"conversation_operation\":\"op-mock-b\",\"order\":" + i +
    ",\"phase\":\"" + m + "\",\"payload\":\"SECRET_OUTPUT_CANARY\"}\n\n"));

async Task<ProviderReceipt> Exercise(string provider, ISyntheticSseDialect dialect,
    Func<HttpResponseMessage> responseFactory,
    Func<ProviderBinding, ProviderBinding>? mutateBinding = null,
    Action<FakeHandler>? inspect = null)
{
    var b = Binding(provider, "session-" + provider);
    if (mutateBinding is not null) b = mutateBinding(b);
    var handler = new FakeHandler(responseFactory);
    using var client = new HttpClient(handler);
    var fixture = new SyntheticApiTransport(client, dialect);
    var result = await fixture.SendAsync(b, Caps(provider), Op(b));
    inspect?.Invoke(handler);
    Check(ProviderReceiptPolicy.Valid(result), "receipt valid: " + provider);
    Check(!result.ContainsContent && result.InputTokens is null && result.OutputTokens is null,
        "receipt redacted: " + provider);
    return result;
}

HttpResponseMessage Response(string data, HttpStatusCode code = HttpStatusCode.OK) =>
    new(code) { Content = new StringContent(data) };

var goodA = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => Response(A("response.output_text.delta", "response.completed")));
Check(goodA.Outcome == ProviderOutcome.Completed, "A completed");
var goodB = await Exercise("mock-b", new MockNonCompatibleDialect(),
    () => Response(B("token", "done")));
Check(goodB.Outcome == ProviderOutcome.Completed, "B completed");
var missing = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => Response(A("response.output_text.delta")));
Check(missing.Outcome == ProviderOutcome.Unknown, "missing terminal unknown");
var incomplete = await Exercise("mock-b", new MockNonCompatibleDialect(),
    () => Response(B("token", "cut_short")));
Check(incomplete.Outcome == ProviderOutcome.Incomplete, "provider incomplete retained");
var badProtocol = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => Response(B("token", "done")));
Check(badProtocol.Outcome == ProviderOutcome.Unknown, "dialect mixup unknown");
var truncated = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => Response(A("response.completed")[..^1]));
Check(truncated.Outcome == ProviderOutcome.Unknown, "missing SSE blank terminator unknown");
var invalid = await Exercise("mock-b", new MockNonCompatibleDialect(),
    () => Response("data: not JSON\n\n"));
Check(invalid.Outcome == ProviderOutcome.Unknown, "invalid JSON unknown");
var dup = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => Response("data: {\"operation\":\"op-mock-a\",\"index\":0,\"type\":\"response.output_text.delta\"}\n\n" +
                   "data: {\"operation\":\"op-mock-a\",\"index\":0,\"type\":\"response.completed\"}\n\n"));
Check(dup.Outcome == ProviderOutcome.Unknown, "duplicate sequence unknown");
var afterTerminal = await Exercise("mock-b", new MockNonCompatibleDialect(),
    () => Response(B("done", "token")));
Check(afterTerminal.Outcome == ProviderOutcome.Unknown, "after terminal refused");
var httpError = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => Response("SECRET_ERROR_BODY_CANARY", HttpStatusCode.TooManyRequests));
Check(httpError.Outcome == ProviderOutcome.Failed && httpError.FailureClass == ProviderFailureClass.Transport,
    "HTTP refusal generic, body not exported");
var cross = await Exercise("mock-b", new MockOpenAiLikeDialect(),
    () => Response("wrong"), inspect: h => Check(h.Calls == 0, "wrong dialect no network"));
Check(cross.Outcome == ProviderOutcome.Refused, "provider dialect mismatch refused");
var endpoint = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => Response("wrong"), mutateBinding: b => b with { EndpointPolicyRef = "cloud" },
    inspect: h => Check(h.Calls == 0, "unsafe endpoint no network"));
Check(endpoint.Outcome == ProviderOutcome.Refused, "unsafe endpoint refused");
var transportFault = await Exercise("mock-a", new MockOpenAiLikeDialect(),
    () => throw new HttpRequestException("SECRET_NETWORK_CANARY"));
Check(transportFault.Outcome == ProviderOutcome.Unknown, "ambiguous transport failure unknown");
Console.WriteLine($"QUALIFIED_SYNTHETIC_TRANSPORT_ONLY assertions={count} dialects=2 provider_network=false secrets=false session_authority_changed=false");

sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage> _response;
    public int Calls { get; private set; }
    public FakeHandler(Func<HttpResponseMessage> response) => _response = response;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (request.RequestUri?.Host != "synthetic.invalid")
            throw new InvalidOperationException("Non-synthetic endpoint forbidden.");
        if (request.Headers.Authorization is not null)
            throw new InvalidOperationException("Credentials forbidden.");
        return Task.FromResult(_response());
    }
}
