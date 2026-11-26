using System.Text.Json;

namespace SupraChat.Core;

internal sealed record PairedWorkspaceSettings(Guid Id, string Name, string? ProjectUrl = null,
    string? StartupDirectory = null, Dictionary<string, string>? EnvironmentOverrides = null,
    double ChatFraction = 0.7, bool ShowTerminal = true);

internal sealed record PairedWorkspaceLayout(Guid? Active, List<PairedWorkspaceSettings> Workspaces)
{
    private static string FilePath => Path.Combine(AppState.DirectoryPath, "paired-workspaces.json");

    internal static PairedWorkspaceLayout Load()
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length <= 1024 * 1024 &&
                JsonSerializer.Deserialize<PairedWorkspaceLayout>(File.ReadAllText(FilePath)) is { } layout &&
                layout.Workspaces is not null)
                return layout with
                {
                    Workspaces = layout.Workspaces
                        .Where(workspace => workspace is not null && workspace.Id != Guid.Empty &&
                            !string.IsNullOrWhiteSpace(workspace.Name))
                        .DistinctBy(workspace => workspace.Id).ToList()
                };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged layout must not prevent the app from opening.
        }
        return new(null, new());
    }

    internal void Save()
    {
        Directory.CreateDirectory(AppState.DirectoryPath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static Uri? ProjectAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
        url.Host == "chatgpt.com" && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) &&
        string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment) &&
        System.Text.RegularExpressions.Regex.IsMatch(url.AbsolutePath, @"^/g/g-p-[a-zA-Z0-9-]+/project/?$")
            ? url : null;
}
