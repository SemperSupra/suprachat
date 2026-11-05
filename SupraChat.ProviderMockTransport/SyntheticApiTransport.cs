using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using SupraChat.ProviderContracts;

namespace SupraChat.ProviderMockTransport;

// SYNTHETIC-ONLY transport qualification, not an operational network client.
// Host/admission semantics are intentionally separate from this disposable parser.
public interface ISyntheticSseDialect
{
    string ProviderId { get; }
    bool TryMap(JsonElement document, string operationId, out ProviderEvent mapped);
}

// Two deliberately DIFFERENT wire formats, so compatibility assumptions are testable.
public sealed class MockOpenAiLikeDialect : ISyntheticSseDialect
{
    public string ProviderId => "mock-a";
    public bool TryMap(JsonElement document, string operationId, out ProviderEvent mapped)
    {
        mapped = default!;
        if (!document.TryGetProperty("operation", out var op) ||
            op.GetString() != operationId ||
            !document.TryGetProperty("index", out var idx) ||
            !idx.TryGetInt64(out var sequence) ||
            !document.TryGetProperty("type", out var type))
            return false;
        var kind = type.GetString() switch
        {
            "response.output_text.delta" => ProviderEventKind.Delta,
            "response.completed" => ProviderEventKind.Completed,
            "response.failed" => ProviderEventKind.Failed,
            "response.incomplete" => ProviderEventKind.Incomplete,
            _ => (ProviderEventKind)(-1)
        };
        if (!Enum.IsDefined(kind)) return false;
        mapped = new(operationId, sequence, kind);
        return true;
    }
}

public sealed class MockNonCompatibleDialect : ISyntheticSseDialect
{
    public string ProviderId => "mock-b";
    public bool TryMap(JsonElement document, string operationId, out ProviderEvent mapped)
    {
        mapped = default!;
        if (!document.TryGetProperty("conversation_operation", out var op) ||
            op.GetString() != operationId ||
            !document.TryGetProperty("order", out var seq) ||
            !seq.TryGetInt64(out var sequence) ||
            !document.TryGetProperty("phase", out var phase))
            return false;
        var kind = phase.GetString() switch
        {
            "token" => ProviderEventKind.Delta,
            "done" => ProviderEventKind.Completed,
            "error" => ProviderEventKind.Failed,
            "cut_short" => ProviderEventKind.Incomplete,
            _ => (ProviderEventKind)(-1)
        };
        if (!Enum.IsDefined(kind)) return false;
        mapped = new(operationId, sequence, kind);
        return true;
    }
}

public sealed class SyntheticApiTransport
{
    // The caller must inject a fake handler through HttpClient. The URI uses the
    // reserved .invalid TLD and this module is never registered in production.
    private static readonly Uri FixtureUri = new("https://synthetic.invalid/test");
    private readonly HttpClient _http;
    private readonly ISyntheticSseDialect _dialect;

    public SyntheticApiTransport(HttpClient http, ISyntheticSseDialect dialect)
    {
        _http = http;
        _dialect = dialect;
    }

    public async Task<ProviderReceipt> SendAsync(
        ProviderBinding binding, ProviderCapabilities capabilities,
        ProviderOperation operation, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        if (binding.Surface != ProviderSurface.Api ||
            binding.EndpointPolicyRef != "synthetic-only" ||
            binding.ProviderId != _dialect.ProviderId ||
            !ProviderAdmission.Evaluate(binding, capabilities, operation).Admitted)
            return Receipt(operation, ProviderOutcome.Refused, ProviderFailureClass.Policy, clock.ElapsedMilliseconds);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, FixtureUri)
            {
                Content = new StringContent("{}") // no prompt, token, credentials or attachments
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Receipt(operation, ProviderOutcome.Failed, ProviderFailureClass.Transport, clock.ElapsedMilliseconds);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var observations = new List<ProviderEvent>();
            string? pending = null;
            string? line;
            var malformed = false;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (line.Length > 2048 || observations.Count > 128)
                {
                    malformed = true;
                    break;
                }
                if (line.Length == 0)
                {
                    if (pending is not null)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(pending);
                            if (!_dialect.TryMap(doc.RootElement, operation.OperationId, out var ev))
                            {
                                malformed = true;
                                break;
                            }
                            observations.Add(ev);
                        }
                        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                        {
                            malformed = true;
                            break;
                        }
                        pending = null;
                    }
                    continue;
                }
                if (!line.StartsWith("data: ", StringComparison.Ordinal) || pending is not null)
                {
                    malformed = true;
                    break;
                }
                pending = line[6..];
            }
            if (pending is not null || malformed || observations.Count > 128)
                return Receipt(operation, ProviderOutcome.Unknown, ProviderFailureClass.Unknown, clock.ElapsedMilliseconds);

            var outcome = ProviderStreamReconciler.Classify(operation.OperationId, observations, transportClosed: true);
            ProviderFailureClass? failure = outcome == ProviderOutcome.Completed ? (ProviderFailureClass?)null :
                outcome == ProviderOutcome.Incomplete ? ProviderFailureClass.Incomplete :
                ProviderFailureClass.Unknown;
            return Receipt(operation, outcome, failure, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Receipt(operation, ProviderOutcome.Cancelled, ProviderFailureClass.Unknown, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            // No provider body or exception message is retained in the receipt.
            return Receipt(operation, ProviderOutcome.Unknown, ProviderFailureClass.Unknown, clock.ElapsedMilliseconds);
        }
    }

    private static ProviderReceipt Receipt(
        ProviderOperation operation, ProviderOutcome outcome, ProviderFailureClass? failure, long duration)
    {
        var receipt = new ProviderReceipt(
            ProviderReceiptPolicy.Schema, operation.OperationId, operation.BindingId,
            outcome, failure, UsageSource.Unknown, null, null, duration, null,
            ProviderRequestAlias: null, ContainsContent: false);
        if (!ProviderReceiptPolicy.Valid(receipt))
            throw new InvalidOperationException("Synthetic receipt violated metadata-only contract.");
        return receipt;
    }
}
