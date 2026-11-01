using System.Text;

namespace SupraChat.Core;

/// <summary>
/// Safe, bounded read-only inspection of git worktree and branch metadata.
/// Never launches a subprocess, executes git commands, or accesses remote networks.
/// Reads at most 512 bytes from local .git pointers.
/// </summary>
public static class WorkspaceMetadataDetector
{
    public const int MaxWorktreeReadBytes = 512;

    public static string? DetectBranch(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return null;
        try
        {
            if (!Directory.Exists(workspacePath)) return null;

            var gitPath = Path.Combine(workspacePath, ".git");
            if (File.Exists(gitPath))
            {
                // This is a git worktree pointer file: "gitdir: /path/to/.git/worktrees/<name>"
                var lines = File.ReadAllLines(gitPath);
                var gitDirLine = lines.FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
                if (gitDirLine is not null)
                {
                    var gitDir = gitDirLine["gitdir:".Length..].Trim();
                    if (!Path.IsPathRooted(gitDir))
                        gitDir = Path.GetFullPath(Path.Combine(workspacePath, gitDir));
                    var headPath = Path.Combine(gitDir, "HEAD");
                    if (File.Exists(headPath))
                        return ParseHeadRef(headPath);
                }
            }
            else if (Directory.Exists(gitPath))
            {
                // Standard git repository directory
                var headPath = Path.Combine(gitPath, "HEAD");
                if (File.Exists(headPath))
                    return ParseHeadRef(headPath);
            }
        }
        catch
        {
            // Fail open: unreadable or corrupt .git metadata returns null without exception
        }
        return null;
    }

    private static string? ParseHeadRef(string headPath)
    {
        using var stream = File.OpenRead(headPath);
        var buffer = new byte[MaxWorktreeReadBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0) return null;
        var text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
        if (text.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            return text["ref: refs/heads/".Length..].Trim();
        if (text.Length >= 7 && text.All(c => char.IsAsciiHexDigit(c)))
            return text[..Math.Min(7, text.Length)];
        return null;
    }
}
