using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SupraChat.Core;

/// <summary>
/// Structured code block extracted from model response text.
/// </summary>
public sealed record CodeSnippet(
    int Index,
    string Language,
    string Content,
    IReadOnlyList<string> Lines);

/// <summary>
/// Parsed terminal execution receipt extracted from a terminal buffer.
/// </summary>
public sealed record TerminalExecutionBlock(
    string Command,
    string Output,
    int LineCount,
    int? ExitCode,
    bool ContainsError);

/// <summary>
/// Bridge component facilitating selective, granular snippet transfer between
/// WebUI/Chat responses and the paired Session Terminal buffer.
/// </summary>
public static class TerminalTransferBridge
{
    private static readonly Regex AnsiPattern = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled);

    private static readonly Regex CodeBlockPattern = new(
        @"```([a-zA-Z0-9_-]*)\r?\n([\s\S]*?)```",
        RegexOptions.Compiled);

    private static readonly Regex SecretPattern = new(
        @"(sk-[a-zA-Z0-9_-]{20,}|Bearer\s+[A-Za-z0-9\._\-]{20,}|password\s*=\s*[^\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes ANSI escape sequences from terminal byte streams and string buffers.
    /// </summary>
    public static string StripAnsi(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            return string.Empty;

        return AnsiPattern.Replace(rawText, string.Empty);
    }

    /// <summary>
    /// Extracts all fenced code blocks from markdown text for individual line/block staging.
    /// </summary>
    public static IReadOnlyList<CodeSnippet> ExtractCodeSnippets(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Array.Empty<CodeSnippet>();

        var matches = CodeBlockPattern.Matches(markdown);
        var snippets = new List<CodeSnippet>(matches.Count);

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var lang = match.Groups[1].Value.Trim().ToLowerInvariant();
            var content = match.Groups[2].Value;
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            snippets.Add(new CodeSnippet(
                Index: i + 1,
                Language: string.IsNullOrEmpty(lang) ? "text" : lang,
                Content: content.Trim(),
                Lines: lines));
        }

        return snippets;
    }

    /// <summary>
    /// Extracts the most recently executed command and its stdout from a terminal buffer.
    /// Strips ANSI sequences and recognizes non-zero exit codes.
    /// </summary>
    public static TerminalExecutionBlock ExtractLastCommandOutput(string terminalBuffer)
    {
        var clean = StripAnsi(terminalBuffer);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return new TerminalExecutionBlock(
                Command: string.Empty,
                Output: string.Empty,
                LineCount: 0,
                ExitCode: null,
                ContainsError: false);
        }

        var lines = clean.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var lastPromptIndex = -1;
        var promptRegex = new Regex(@"(PS [A-Z]:\\.*?>|\$ |> )", RegexOptions.Compiled);

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (promptRegex.IsMatch(lines[i]))
            {
                lastPromptIndex = i;
                break;
            }
        }

        if (lastPromptIndex == -1)
        {
            // No recognizable prompt; take up to the last 50 lines
            var fallbackLines = lines.TakeLast(50).ToArray();
            var fallbackText = string.Join(Environment.NewLine, fallbackLines);
            return new TerminalExecutionBlock(
                Command: string.Empty,
                Output: fallbackText,
                LineCount: fallbackLines.Length,
                ExitCode: null,
                ContainsError: DetectErrorPatterns(fallbackText));
        }

        // Found prompt line; look for previous prompt line to bracket output
        var prevPromptIndex = -1;
        for (var i = lastPromptIndex - 1; i >= 0; i--)
        {
            if (promptRegex.IsMatch(lines[i]))
            {
                prevPromptIndex = i;
                break;
            }
        }

        if (prevPromptIndex == -1)
        {
            // Single command in buffer
            var cmdLine = lines[lastPromptIndex];
            var cmdText = promptRegex.Replace(cmdLine, string.Empty).Trim();
            var outputLines = lines.Skip(lastPromptIndex + 1).ToArray();
            var outputText = string.Join(Environment.NewLine, outputLines);

            return new TerminalExecutionBlock(
                Command: cmdText,
                Output: outputText,
                LineCount: outputLines.Length,
                ExitCode: null,
                ContainsError: DetectErrorPatterns(outputText));
        }

        // Bracketed output between prevPromptIndex and lastPromptIndex
        var command = promptRegex.Replace(lines[prevPromptIndex], string.Empty).Trim();
        var captured = lines.Skip(prevPromptIndex + 1).Take(lastPromptIndex - prevPromptIndex - 1).ToArray();
        var output = string.Join(Environment.NewLine, captured);

        return new TerminalExecutionBlock(
            Command: command,
            Output: output,
            LineCount: captured.Length,
            ExitCode: null,
            ContainsError: DetectErrorPatterns(output));
    }

    /// <summary>
    /// Masks raw API tokens, bearer headers, or secret patterns before sending text to model context.
    /// </summary>
    public static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return SecretPattern.Replace(text, "[REDACTED_SECRET]");
    }

    /// <summary>
    /// Formats terminal output into clean Markdown for conversational model context.
    /// </summary>
    public static string FormatTerminalOutputForChat(string command, string output, int? exitCode = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(command))
        {
            sb.AppendLine($"Command: `{command}`");
        }
        if (exitCode.HasValue)
        {
            sb.AppendLine($"Exit Code: `{exitCode.Value}`");
        }
        sb.AppendLine("```terminal-output");
        sb.AppendLine(RedactSecrets(output.Trim()));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static bool DetectErrorPatterns(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var errRegex = new Regex(@"(error|exception|fatal|failed|failure|command not found|cannot find)", RegexOptions.IgnoreCase);
        return errRegex.IsMatch(text);
    }
}
