namespace SupraChat.Core;

/// <summary>Fail-closed classification; not authentication or an actuator.</summary>
public static class TraceControlBoundary
{
    public static bool CanManageMetadata(TraceOperation operation) =>
        operation is TraceOperation.StartMetadata or TraceOperation.Stop or
            TraceOperation.Read or TraceOperation.ExportMetadata;

    // A future authenticated peer/session grant cannot be inferred from an enum or caller label.
    // The existing local trace controller is the only metadata execution boundary.
    public static bool RequiresSeparateAuthorization(TraceOperation operation) =>
        operation is TraceOperation.CancelProvider or TraceOperation.CapturePayload or
            TraceOperation.ModifyFirstParty;

    public static bool IsActuationAdmitted(TraceOperation operation, TraceSurface surface) => false;
}
