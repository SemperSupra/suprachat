using System.IO.Pipes;
using System.Text.Json;

namespace SupraChat.SessionCore;

/// <summary>
/// Local-user, explicit-connection consumer of the single shared RDTE host.
/// Does not create its own registry, read the store directly, or grant actor authority.
/// </summary>
public sealed class SharedSessionClient
{
    public static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;

    public SharedSessionClient(string pipeName)
    {
        if (!IsSafePipeName(pipeName))
            throw new ArgumentException("INVALID_PIPE_NAME", nameof(pipeName));
        _pipeName = pipeName;
    }

    public async Task<SharedSessionClientReply> RequestAsync(
        SessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(request, WireJson);
        if (payload.Length > 16_384)
            throw new ArgumentException("INVALID_REQUEST_LENGTH", nameof(request));

        using var client = new NamedPipeClientStream(".", _pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(client, leaveOpen: true);
        await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(payload.AsMemory(), deadline.Token).ConfigureAwait(false);
        // The host owns the only authoritative local state: never read a
        // local copy of session records on transport errors or timeout.
        var response = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false)
            ?? throw new IOException("SHARED_SESSION_HOST_NO_REPLY");
        if (response.Length > 262_144)
            throw new InvalidDataException("SHARED_SESSION_REPLY_TOO_LARGE");
        var parsed = JsonSerializer.Deserialize<SharedSessionClientReply>(response, WireJson);
        return parsed ?? throw new InvalidDataException("SHARED_SESSION_BAD_REPLY");
    }

    public static bool IsSafePipeName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length is >= 8 and <= 96 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

public sealed record SharedSessionClientReply(
    bool Ok, string? Error, JsonElement? Result, bool Replayed
);
