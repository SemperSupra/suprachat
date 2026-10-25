using System.Text.Json;

namespace SupraChat.Core;

/// <summary>Explicit local aggregate save. Never prompts for a path or overwrites an existing file.</summary>
internal sealed class NativeMetadataSummaryStore
{
    private readonly string _directory;
    internal NativeMetadataSummaryStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Absolute metadata output directory required.");
        _directory = Path.GetFullPath(directory);
    }
    internal string Save(JsonElement summary)
    {
        if (summary.GetProperty("schema").GetString() != "suprachat-first-party-metadata-hil-summary/v2")
            throw new InvalidDataException("Aggregate metadata summary required.");
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"suprachat-metadata-summary-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, summary, new JsonSerializerOptions { WriteIndented = true });
        return path;
    }
}
