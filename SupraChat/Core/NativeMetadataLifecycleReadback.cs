using System.Text.Json;

namespace SupraChat.Core;

/// <summary>
/// Bounded, non-authoritative view of native metadata lifecycle transitions.
/// The consent session and durable aggregate remain the authorities. Never store
/// session/actor IDs, paths, request URLs, payloads, credentials or raw errors.
/// </summary>
internal sealed class NativeMetadataLifecycleReadback
{
    internal enum Action { Opened, Prepared, Confirmed, Refused, StopRequested, SaveRequested, SummarySaved, SaveFailed, CloseRequested, Closed, Observed }
    internal sealed record Entry(long ElapsedMs, string Action, string State, string EndReason,
        bool Active, bool ObserverAttached, bool CleanupComplete, bool EnableCompleted, bool DisableCompleted);
    private const int Limit = 24;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly List<Entry> _entries = new();
    private string? _lastObserved;
    private readonly string _buildVersion;

    internal NativeMetadataLifecycleReadback(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
        _buildVersion = typeof(NativeMetadataLifecycleReadback).Assembly.GetName().Version?.ToString() ?? "UNAVAILABLE";
    }

    internal IReadOnlyList<Entry> Entries => _entries.AsReadOnly();

    internal void Record(Action action, NativeMetadataStatus? status)
    {
        var state = status?.Consent.State switch
        {
            "ACTIVE" => "ACTIVE",
            "OFF" => "OFF",
            "STOPPED" => "STOPPED",
            "EXPIRED" => "EXPIRED",
            "VIEW_CHANGED" => "VIEW_CHANGED",
            "RECORDER_STOPPED" => "RECORDER_STOPPED",
            "RECORDER_UNAVAILABLE" => "RECORDER_UNAVAILABLE",
            null => "UNKNOWN",
            _ => "UNKNOWN"
        };
        var end = status?.EndReason.ToString() ?? "None";
        if (!Enum.IsDefined(status?.EndReason ?? NativeCaptureEndReason.None)) end = "Unknown";
        var entry = new Entry(
            Math.Max(0, (long)_clock.GetElapsedTime(_started).TotalMilliseconds),
            action.ToString(), state, end,
            status?.Consent.CaptureActive == true, status?.ObserverAttached == true,
            status?.CleanupComplete == true, status?.EnableCompleted == true, status?.DisableCompleted == true);
        // The UI polls frequently; only real transitions create automatic entries.
        var fingerprint = $"{entry.State}|{entry.EndReason}|{entry.Active}|{entry.ObserverAttached}|{entry.CleanupComplete}|{entry.EnableCompleted}|{entry.DisableCompleted}";
        if (action == Action.Observed && string.Equals(fingerprint, _lastObserved, StringComparison.Ordinal)) return;
        _lastObserved = fingerprint;
        _entries.Add(entry);
        if (_entries.Count > Limit) _entries.RemoveAt(0);
    }

    internal string ToUiValue() => JsonSerializer.Serialize(new
    {
        schema = "suprachat-native-lifecycle-readback/v1",
        build_version = _buildVersion,
        completeness = "UNKNOWN",
        is_authoritative_for_consent = false,
        events = _entries
    });
}
