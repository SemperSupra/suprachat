using System.IO.Pipes;
using System.Text.Json;
using SupraChat.SessionCore;

internal static class Program
{
    // Dedicated local-user IPC, NOT a remote transport or authenticated
    // multi-user server. No external model/browser/terminal provider calls.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0] == "serve" && args[1] == "--pipe")
                return await ServeAsync(args[2], args[3]);
            if (args.Length == 3 && args[0] == "call")
                return await CallAsync(args[1], args[2]);

            Console.Error.WriteLine("USAGE: serve --pipe NAME ABSOLUTE_STORE_PATH | call NAME JSON_REQUEST");
            return 2;
        }
        catch (Exception ex)
        {
            // Do not print requester content or secret-bearing error messages.
            Console.Error.WriteLine("SHARED_SESSION_HOST_ERROR: " + ex.GetType().Name);
            return 3;
        }
    }

    private static async Task<int> ServeAsync(string pipeName, string storePath)
    {
        if (!IsSafePipeName(pipeName) || !Path.IsPathFullyQualified(storePath))
            throw new ArgumentException("INVALID_HOST_ARGUMENTS");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
        // Exclusive lifetime lock prevents two divergent hosts from owning
        // the same local operation history.
        using var exclusive = new FileStream(storePath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var registry = new SharedSessionRegistry(storePath);
        Console.WriteLine(JsonSerializer.Serialize(new {
            ready = true, phase = "SYNTHETIC_LOCAL_RDTE", pipe = pipeName
        }, Json));
        Console.Out.Flush();

        while (true)
        {
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync();
                // One connection handles one request. Separate clients can be
                // active simultaneously, but each mutation commits atomically.
                _ = Task.Run(() => HandleAsync(server, registry));
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream server, SharedSessionRegistry registry)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, leaveOpen: true);
                await using var writer = new StreamWriter(server, leaveOpen: true)
                {
                    AutoFlush = true
                };
                var line = await reader.ReadLineAsync();
                if (line is null || line.Length > 16_384)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new SessionReply(false, "INVALID_REQUEST_LENGTH", null, false), Json));
                    return;
                }
                SessionReply reply;
                try
                {
                    var request = JsonSerializer.Deserialize<SessionRequest>(line, Json);
                    reply = request is null
                        ? new SessionReply(false, "INVALID_REQUEST", null, false)
                        : registry.Execute(request);
                }
                catch (JsonException)
                {
                    reply = new SessionReply(false, "INVALID_REQUEST_JSON", null, false);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json));
            }
            catch (IOException)
            {
                // Client may have disconnected. A committed mutation remains
                // replayable from the operation ID after process reconnect.
            }
            catch (Exception)
            {
                // Persistence uncertainty is fail-stop, not an excuse to emit
                // success or admit further mutations in this process.
                Environment.FailFast("SHARED_SESSION_HOST_FAIL_CLOSED");
            }
        }
    }

    private static async Task<int> CallAsync(string pipeName, string jsonRequest)
    {
        if (!IsSafePipeName(pipeName) || jsonRequest.Length > 16_384)
            throw new ArgumentException("INVALID_CLIENT_ARGUMENTS");
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        using var reader = new StreamReader(client, leaveOpen: true);
        await using var writer = new StreamWriter(client, leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(jsonRequest);
        var result = await reader.ReadLineAsync();
        if (result is null) throw new IOException("NO_REPLY");
        Console.WriteLine(result);
        return 0;
    }

    private static bool IsSafePipeName(string pipeName) =>
        pipeName.Length is >= 8 and <= 96 &&
        pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
