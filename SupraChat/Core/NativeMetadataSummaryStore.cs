using System.Text.Json;

namespace SupraChat.Core;

/// <summary>Explicit local aggregate save. Never prompts for a path or overwrites an existing file.</summary>
internal sealed class NativeMetadataSummaryStore
{
    private readonly string _directory;
    private readonly bool _synthetic;
    internal NativeMetadataSummaryStore(string directory, bool synthetic = false)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Absolute metadata output directory required.");
        _directory = Path.GetFullPath(directory);
        _synthetic = synthetic;
    }
    internal string Save(JsonElement summary)
    {
        var schema = summary.GetProperty("schema").GetString();
        var allowed = _synthetic
            ? schema is "suprachat-synthetic-native-metadata-summary/v1" or "suprachat-synthetic-native-metadata-summary/v2"
            : schema is "suprachat-first-party-metadata-hil-summary/v2" or "suprachat-first-party-metadata-hil-summary/v3";
        if (!allowed) throw new InvalidDataException("Aggregate metadata summary required.");
        // New versions must carry exactly the app-authored lifecycle extension.
        if (schema is "suprachat-synthetic-native-metadata-summary/v2" or "suprachat-first-party-metadata-hil-summary/v3")
        {
            if (!summary.TryGetProperty("lifecycle_readback", out var history) ||
                history.ValueKind != JsonValueKind.Object ||
                history.GetProperty("schema").GetString() != "suprachat-native-lifecycle-readback/v1")
                throw new InvalidDataException("Versioned lifecycle history missing.");
        }
        Directory.CreateDirectory(_directory);
        var prefix = _synthetic ? "suprachat-synthetic-metadata-summary" : "suprachat-metadata-summary";
        var path = Path.Combine(_directory, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        // Serialize and sync in the destination directory before the final
        // no-overwrite rename. Failed serialization must never leave a partial
        // *.json aggregate that a later qualification could mistake for evidence.
        // .pending is a transient staging file, not a separate history ledger.
        var pending = Path.Combine(_directory, $".{prefix}-{Guid.NewGuid():N}.pending");
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, summary, new JsonSerializerOptions { WriteIndented = true });
                output.Flush(flushToDisk: true);
            }
            File.Move(pending, path); // Same directory; refuses a preexisting destination.
            return path;
        }
        finally
        {
            // On failure, keep the original summary untouched. This best-effort
            // cleanup cannot roll back an already committed final file.
            if (File.Exists(pending)) File.Delete(pending);
        }
    }
}
