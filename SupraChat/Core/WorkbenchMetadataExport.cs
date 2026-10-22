using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// Pure, metadata-only export preview. Does not read transcripts, browser profiles,
/// terminal buffers, credentials or transfer draft text. No filesystem effects.
/// The caller must show this exact content for explicit human approval before save.
/// </summary>
public static class WorkbenchMetadataExport
{
    public const string Schema = "suprachat-workbench-metadata-export/v1";

    public static WorkbenchMetadataPreview Prepare(WorkbenchSnapshot snapshot, string format)
    {
        if (snapshot.Schema != SessionWorkbench.Schema ||
            snapshot.Sessions.Count > SessionWorkbench.MaximumSessions)
            throw new ArgumentException("INVALID_WORKBENCH_SNAPSHOT", nameof(snapshot));
        if (format is not ("json" or "markdown"))
            throw new ArgumentException("UNSUPPORTED_EXPORT_FORMAT", nameof(format));

        var content = format == "json" ? FormatJson(snapshot) : FormatMarkdown(snapshot);
        return new WorkbenchMetadataPreview(Schema, format, content,
            Encoding.UTF8.GetByteCount(content), WasSaved: false,
            RequiresUserSelectedDestination: true,
            "Contains conversation URLs and workspace references. Review before saving. No transcript, transfer text, terminal output, or credential material is included.");
    }

    private static string FormatJson(WorkbenchSnapshot snapshot)
    {
        var data = new
        {
            schema = Schema,
            export_scope = "SESSION_METADATA_ONLY",
            active_session_id = snapshot.ActiveSessionId,
            sessions = snapshot.Sessions.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                conversation_url = s.ConversationUrl,
                workspace_key = s.WorkspaceKey,
                terminal_state = s.TerminalState,
                terminal_execution_permitted = false,
                chat_submission_permitted = false
            }).ToArray(),
            collisions = snapshot.Collisions.Select(c => new
            {
                type = c.Type,
                left_session_id = c.LeftSessionId,
                right_session_id = c.RightSessionId
            }).ToArray(),
            exported_transcripts = false,
            exported_terminal_output = false,
            exported_transfer_previews = false,
            exported_credentials = false
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string FormatMarkdown(WorkbenchSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SupraChat session metadata");
        sb.AppendLine();
        sb.AppendLine("Scope: SESSION_METADATA_ONLY. Not a transcript, terminal log, or executable transfer.");
        sb.AppendLine();
        sb.AppendLine("| Session | ID | Conversation reference | Workspace reference | Terminal |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var session in snapshot.Sessions)
        {
            sb.Append("| ").Append(Cell(session.Title))
              .Append(" | ").Append(Cell(session.Id))
              .Append(" | ").Append(Cell(session.ConversationUrl ?? "Not connected"))
              .Append(" | ").Append(Cell(session.WorkspaceKey ?? "Not set"))
              .Append(" | ").Append(Cell(session.TerminalState)).AppendLine(" |");
        }
        sb.AppendLine();
        sb.AppendLine("## Collision warnings");
        if (snapshot.Collisions.Count == 0)
        {
            sb.AppendLine("None observed.");
        }
        else
        {
            foreach (var collision in snapshot.Collisions)
            {
                sb.Append("- ").Append(Cell(collision.Type)).Append(": ")
                  .Append(Cell(collision.LeftSessionId)).Append(" ↔ ")
                  .Append(Cell(collision.RightSessionId)).AppendLine();
            }
        }
        sb.AppendLine();
        sb.Append("Active session: ").AppendLine(Cell(snapshot.ActiveSessionId ?? "None"));
        sb.AppendLine("Chat submission: disabled. Terminal execution: disabled.");
        sb.AppendLine("Excluded: credentials, transcripts, terminal output, and transfer preview text.");
        return sb.ToString();
    }

    private static string Cell(string value)
    {
        // No unsanitized Markdown table pipes or control/newline injection.
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '|': builder.Append("\\|"); break;
                case '\r':
                case '\n': builder.Append(' '); break;
                default: builder.Append(char.IsControl(ch) ? ' ' : ch); break;
            }
        }
        return builder.ToString();
    }
}

public sealed record WorkbenchMetadataPreview(
    string Schema, string Format, string Content, int ContentBytes,
    bool WasSaved, bool RequiresUserSelectedDestination, string DataWarning);
