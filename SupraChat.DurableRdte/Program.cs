using System.Text.Json;
using SupraChat.DurableRdte;

internal static class Program
{
    // One explicitly launched owner per store. This is an isolated synthetic
    // test protocol, NOT a ChatGPT provider client or production network host.
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "serve" || !Path.IsPathFullyQualified(args[1]))
        {
            Console.Error.WriteLine("USAGE: serve ABSOLUTE_DISPOSABLE_TEST_STORE_PATH");
            return 2;
        }
        var file = Path.GetFullPath(args[1]);
        try
        {
            var dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);
            // No check-then-open race for the owner lock. A second local host
            // MUST not start and split authority across two writers.
            using var lockFile = new FileStream(file+".ownerlock",
                FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var ledger = new DurableLedger(file);
            Console.WriteLine(JsonSerializer.Serialize(new {
                schema=DurableLedger.Schema,ready=true,sqlite_wal_full=true,
                provider_contacted=false,actor_identity_authenticated=false,
                production_host_integrated=false
            }));
            Console.Out.Flush();
            string? line;
            while((line=Console.ReadLine()) is not null)
            {
                if(line.Length>4096)
                {
                    Console.WriteLine("{\"error\":\"INVALID_REQUEST_LENGTH\"}");
                    continue;
                }
                try
                {
                    if(line=="{\"action\":\"integrity\"}")
                    {
                        Console.WriteLine(JsonSerializer.Serialize(ledger.InspectIntegrity()));
                        continue;
                    }
                    var req=JsonSerializer.Deserialize<DurableRequest>(line,
                        new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
                    if(req is null) throw new ArgumentException("INVALID_REQUEST");
                    if(req.Action=="integrity")
                    {
                        Console.WriteLine(JsonSerializer.Serialize(ledger.InspectIntegrity()));
                        continue;
                    }
                    var result=ledger.Execute(req);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                }
                catch(ArgumentException)
                {
                    Console.WriteLine("{\"error\":\"INVALID_REQUEST\"}");
                }
                catch(JsonException)
                {
                    Console.WriteLine("{\"error\":\"INVALID_JSON\"}");
                }
                catch(Exception)
                {
                    // Includes SQLite I/O/storage/corruption. Never admit a
                    // future write after an uncertain persistence failure.
                    Environment.Exit(78);
                }
                Console.Out.Flush();
            }
            return 0;
        }
        catch(Exception)
        {
            Console.Error.WriteLine("DURABLE_RDTE_FAIL_CLOSED");
            return 78;
        }
    }
}
