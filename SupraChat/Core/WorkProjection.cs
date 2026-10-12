using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupraChat.Core;

public static class WorkProjectionEngine
{
    public const string SnapshotSchema = "suprachat-authoritative-work-snapshot/v1";
    public const string ProjectionSchema = "suprachat-work-projection/v1";
    public const string ControllerObservationSchema = "suprachat-controller-observation/v1";

    // Provider attempt states never establish owner acceptance.
    private static readonly HashSet<string> AllowedExecutionStates = new(StringComparer.Ordinal)
    {
        "UNKNOWN", "READY", "RUNNING", "WAITING", "BLOCKED",
        "COMPLETED", "FAILED", "CANCELLED"
    };

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

    public static ControllerObservation ParseControllerObservation(string json)
    {
        var observation = JsonSerializer.Deserialize<ControllerObservation>(json, JsonOptions)
            ?? throw new InvalidOperationException("Controller observation JSON was empty.");
        ValidateObservation(observation);
        return observation;
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
        ProjectionContext context,
        ControllerObservation? observation = null)
    {
        Validate(snapshot);
        ControllerExecutionProjection? controller = null;
        if (observation is not null)
        {
            ValidateObservation(observation);
            if (!string.Equals(observation.WorkstreamId, snapshot.WorkstreamId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Controller observation workstream does not match the owning snapshot.");

            // The caller must bind this observation explicitly to its owner. The sidecar
            // delegation projection is not an authoritative workstream source. No controller
            // text, source locator, generation or actuation grant crosses this boundary.
            if (context.AllowedInformationClasses.Contains(observation.InformationClass))
            {
                controller = new ControllerExecutionProjection(
                    observation.DelegationId,
                    observation.ExecutionState,
                    Freshness: "UNKNOWN_NO_SOURCE_REVISION",
                    Acceptance: "NOT_ESTABLISHED",
                    RequiresPointOfUseReconciliation: true);
            }
        }

        var classVisibleItems = snapshot.Items
            .Where(x => context.AllowedInformationClasses.Contains(x.InformationClass))
            .ToArray();
        var visibleItems = classVisibleItems
            .Where(x => context.RichDetail || x.Material)
            .Select(x => context.RichDetail
                ? x
                : x with { EvidenceRefs = Array.Empty<string>() })
            .ToArray();

        var classVisibleCaptures = snapshot.Captures
            .Where(x => context.AllowedInformationClasses.Contains(x.InformationClass))
            .ToArray();
        var visibleCaptures = classVisibleCaptures;

        var classVisibleActions = snapshot.Actions
            .Where(x => context.AllowedInformationClasses.Contains(x.InformationClass))
            .ToArray();
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
            classVisibleItems.Length - visibleItems.Length,
            snapshot.Items.Length - classVisibleItems.Length,
            snapshot.Captures.Length - classVisibleCaptures.Length,
            snapshot.Actions.Length - visibleActions.Length,
            controller,
            observation is not null && controller is null ? 1 : 0);
    }

    private static void ValidateObservation(ControllerObservation observation)
    {
        if (!string.Equals(observation.Schema, ControllerObservationSchema, StringComparison.Ordinal) ||
            !string.Equals(observation.SourceSystem, "agent-dispatch", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(observation.WorkstreamId) ||
            string.IsNullOrWhiteSpace(observation.DelegationId) ||
            !AllowedExecutionStates.Contains(observation.ExecutionState) ||
            observation.InformationClass is not ("public" or "internal" or "restricted"))
            throw new InvalidOperationException(
                "Controller observation lacks a supported schema, identity, source, state or disclosure class.");
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

        if (snapshot.Source is null ||
            string.IsNullOrWhiteSpace(snapshot.Source.System) ||
            string.IsNullOrWhiteSpace(snapshot.Source.Locator) ||
            string.IsNullOrWhiteSpace(snapshot.Source.Generation) ||
            snapshot.Items is null ||
            snapshot.Captures is null ||
            snapshot.Actions is null)
            throw new InvalidOperationException(
                "Work snapshot must include source identity plus item, capture, and action arrays.");

        foreach (var datum in snapshot.Items)
        {
            if (string.IsNullOrWhiteSpace(datum.Id) ||
                string.IsNullOrWhiteSpace(datum.Kind) ||
                string.IsNullOrWhiteSpace(datum.InformationClass) ||
                datum.EvidenceRefs is null)
                throw new InvalidOperationException("Work snapshot contains an invalid item.");
        }

        foreach (var capture in snapshot.Captures)
        {
            if (string.IsNullOrWhiteSpace(capture.Id) ||
                string.IsNullOrWhiteSpace(capture.Kind) ||
                string.IsNullOrWhiteSpace(capture.InformationClass) ||
                capture.RelatedWorkstreams is null ||
                capture.Constraints is null)
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
    int SuppressedDetailItemCount,
    int RestrictedItemCount,
    int RestrictedCaptureCount,
    int UnavailableActionCount,
    ControllerExecutionProjection? Controller = null,
    int RestrictedControllerObservationCount = 0);

// Normalized, explicitly owner-bound, ephemeral input. It is not a GitHub/Sidecar
// adapter and does not assert source freshness or authorization for any command.
public sealed record ControllerObservation(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("workstream_id")] string WorkstreamId,
    [property: JsonPropertyName("source_system")] string SourceSystem,
    [property: JsonPropertyName("delegation_id")] string DelegationId,
    [property: JsonPropertyName("execution_state")] string ExecutionState,
    [property: JsonPropertyName("information_class")] string InformationClass);

public sealed record ControllerExecutionProjection(
    string DelegationId,
    string ExecutionState,
    string Freshness,
    string Acceptance,
    bool RequiresPointOfUseReconciliation);
