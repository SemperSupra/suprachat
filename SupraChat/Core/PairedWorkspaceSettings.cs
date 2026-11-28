using System.Text.Json;

namespace SupraChat.Core;

internal sealed record PairedWorkspaceSettings(Guid Id, string Name, string? ProjectUrl = null,
    string? StartupDirectory = null, Dictionary<string, string>? EnvironmentOverrides = null,
    double ChatFraction = 0.7, bool ShowTerminal = true, bool StackPanes = false,
    double StackedChatFraction = 0.58, string? DurableIssue = null);

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

    internal static string? IssueLocator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(value.Trim(),
            @"\A(?:https://github\.com/(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)/issues/(?<issue>[1-9][0-9]*)/?|(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)#(?<issue>[1-9][0-9]*))\z");
        if (!match.Success || !int.TryParse(match.Groups["issue"].Value, out var number))
            return null;
        var repository = match.Groups["repo"].Value;
        if (repository.Split('/').Any(segment => segment is "." or ".."))
            return null;
        return $"{repository}#{number}";
    }
}

public static class SavedWorkspaceInventory
{
    public static object Read()
    {
        var layout = PairedWorkspaceLayout.Load();
        return new
        {
            schema = "suprachat-saved-workspaces/v1",
            scope = "saved-local-configuration",
            active_id = layout.Workspaces.Any(workspace => workspace.Id == layout.Active) ? layout.Active : null,
            workspaces = layout.Workspaces.Select(workspace => new
            {
                id = workspace.Id,
                name = workspace.Name,
                layout = workspace.StackPanes ? "stacked" : "side-by-side",
                show_terminal = workspace.ShowTerminal,
                project_pinned = PairedWorkspaceLayout.ProjectAddress(workspace.ProjectUrl) is not null,
                durable_issue_configured = PairedWorkspaceLayout.IssueLocator(workspace.DurableIssue) is not null,
                startup_directory_configured = !string.IsNullOrEmpty(workspace.StartupDirectory),
                environment_overrides_configured = workspace.EnvironmentOverrides is { Count: > 0 }
            }).ToArray()
        };
    }
}
