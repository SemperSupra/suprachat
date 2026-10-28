namespace SupraChat.Core;

internal enum NativeMetadataPreparationReason
{
    AcknowledgementRequired, NativeViewUnavailable, NativeViewChanged, OriginNotReady,
    EnvironmentOverride, NativeInteropUnavailable, ReviewNotReady
}
internal sealed class NativeMetadataPreparationException(NativeMetadataPreparationReason reason)
    : InvalidOperationException("Native metadata preparation unavailable.")
{
    internal NativeMetadataPreparationReason Reason { get; } = reason;
}
