using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupraChat.Core;

public static class WorkProjectionEngine
{
    public const string SnapshotSchema = "suprachat-authoritative-work-snapshot/v1";
    public const string ProjectionSchema = "suprachat-work-projection/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static AuthoritativeWorkSnapshot LoadSnapshot(string path) =>
        ParseSnapshot(File.ReadAllText(path));

    public static AuthoritativeWorkSnapshot ParseSnapshot(string json)
    {
        var snapshot = JsonSerializer.Deserialize<AuthoritativeWorkSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException("Work snapshot JSON was empty.");
        Validate(snapshot);
        return snapshot;
    }

    public static ProjectionContext ContextForProfile(string profile) =>
        profile.ToLowerInvariant() switch
        {
            "rich" => new ProjectionContext(
                "rich",
                RichDetail: true,
                AllowedInformationClasses: new HashSet<string>(StringComparer.Ordinal)
                {
                    "public", "internal", "restricted"
                },
                AllowedActionIds: null),
            "compact" => new ProjectionContext(
                "compact",
                RichDetail: false,
                AllowedInformationClasses: new HashSet<string>(StringComparer.Ordinal)
                {
                    "public", "internal", "restricted"
                },
                AllowedActionIds: null),
            "restricted" => new ProjectionContext(
                "restricted",
                RichDetail: false,
                AllowedInformationClasses: new HashSet<string>(StringComparer.Ordinal)
                {
                    "public"
                },
                AllowedActionIds: new HashSet<string>(StringComparer.Ordinal)
                {
                    "capture.add",
                    "work.defer",
                    "work.inspect.public"
                }),
            _ => throw new ArgumentException(
                $"Unsupported projection profile '{profile}'. Use rich, compact, or restricted.",
                nameof(profile))
        };

    public static HumanWorkProjection Project(
        AuthoritativeWorkSnapshot snapshot,
        ProjectionContext context)
    {
        Validate(snapshot);

        var visibleItems = snapshot.Items
            .Where(x => context.AllowedInformationClasses.Contains(x.InformationClass))
            .Where(x => context.RichDetail || x.Material)
            .Select(x => context.RichDetail
                ? x
                : x with { EvidenceRefs = Array.Empty<string>() })
            .ToArray();

        var visibleCaptures = snapshot.Captures
            .Where(x => context.AllowedInformationClasses.Contains(x.InformationClass))
            .ToArray();

        var classVisibleActions = snapshot.Actions
            .Where(x => context.AllowedInformationClasses.Contains(x.InformationClass));
        var visibleActions = (context.AllowedActionIds is null
                ? classVisibleActions
                : classVisibleActions.Where(x => context.AllowedActionIds.Contains(x.Id)))
            .ToArray();

        return new HumanWorkProjection(
            ProjectionSchema,
            snapshot.WorkstreamId,
            snapshot.Source,
            snapshot.ObservedAt,
            context.Profile,
            snapshot.Mission,
            snapshot.AuthoritativeState,
            snapshot.Frontier,
            snapshot.Disposition,
            visibleItems,
            visibleCaptures,
            visibleActions,
            snapshot.Items.Length - visibleItems.Length,
            snapshot.Captures.Length - visibleCaptures.Length,
            snapshot.Actions.Length - visibleActions.Length);
    }

    private static void Validate(AuthoritativeWorkSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Schema, SnapshotSchema, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Unsupported work snapshot schema '{snapshot.Schema}'.");

        if (string.IsNullOrWhiteSpace(snapshot.WorkstreamId) ||
            string.IsNullOrWhiteSpace(snapshot.Mission) ||
            string.IsNullOrWhiteSpace(snapshot.AuthoritativeState) ||
            string.IsNullOrWhiteSpace(snapshot.Frontier))
            throw new InvalidOperationException(
                "Work snapshot must identify its workstream, mission, authoritative state, and frontier.");

        foreach (var datum in snapshot.Items)
        {
            if (string.IsNullOrWhiteSpace(datum.Id) ||
                string.IsNullOrWhiteSpace(datum.Kind) ||
                string.IsNullOrWhiteSpace(datum.InformationClass))
                throw new InvalidOperationException("Work snapshot contains an invalid item.");
        }

        foreach (var capture in snapshot.Captures)
        {
            if (string.IsNullOrWhiteSpace(capture.Id) ||
                string.IsNullOrWhiteSpace(capture.Kind) ||
                string.IsNullOrWhiteSpace(capture.InformationClass))
                throw new InvalidOperationException("Work snapshot contains an invalid capture.");
        }

        foreach (var action in snapshot.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Id) ||
                string.IsNullOrWhiteSpace(action.Intent) ||
                string.IsNullOrWhiteSpace(action.InformationClass))
                throw new InvalidOperationException("Work snapshot contains an invalid action.");
        }
    }
}

public sealed record ProjectionContext(
    string Profile,
    bool RichDetail,
    IReadOnlySet<string> AllowedInformationClasses,
    IReadOnlySet<string>? AllowedActionIds);

public sealed record AuthoritativeWorkSnapshot(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("workstream_id")] string WorkstreamId,
    [property: JsonPropertyName("source")] WorkProjectionSource Source,
    [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("mission")] string Mission,
    [property: JsonPropertyName("authoritative_state")] string AuthoritativeState,
    [property: JsonPropertyName("frontier")] string Frontier,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("items")] WorkProjectionDatum[] Items,
    [property: JsonPropertyName("captures")] WorkProjectionCapture[] Captures,
    [property: JsonPropertyName("actions")] WorkProjectionAction[] Actions);

public sealed record WorkProjectionSource(
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("locator")] string Locator,
    [property: JsonPropertyName("generation")] string Generation);

public sealed record WorkProjectionDatum(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("information_class")] string InformationClass,
    [property: JsonPropertyName("material")] bool Material,
    [property: JsonPropertyName("evidence_refs")] string[] EvidenceRefs);

public sealed record WorkProjectionCapture(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("information_class")] string InformationClass,
    [property: JsonPropertyName("related_workstreams")] string[] RelatedWorkstreams,
    [property: JsonPropertyName("constraints")] string[] Constraints,
    [property: JsonPropertyName("promoted")] bool Promoted,
    [property: JsonPropertyName("execution_authorized")] bool ExecutionAuthorized);

public sealed record WorkProjectionAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("information_class")] string InformationClass,
    [property: JsonPropertyName("consequential")] bool Consequential);

public sealed record HumanWorkProjection(
    string Schema,
    string WorkstreamId,
    WorkProjectionSource Source,
    DateTimeOffset ObservedAt,
    string Profile,
    string Mission,
    string AuthoritativeState,
    string Frontier,
    string Disposition,
    WorkProjectionDatum[] Items,
    WorkProjectionCapture[] Captures,
    WorkProjectionAction[] AllowedActions,
    int WithheldItemCount,
    int WithheldCaptureCount,
    int WithheldActionCount);
