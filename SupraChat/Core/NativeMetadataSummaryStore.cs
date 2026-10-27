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
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, summary, new JsonSerializerOptions { WriteIndented = true });
        return path;
    }
}
