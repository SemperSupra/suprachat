using System.Globalization;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SupraChat.Core;

namespace SupraChat;

/// <summary>
/// W16 adds no additional session registry or provider authority.
/// It observes only the same W6 localhost GET used by the W14 manual UI.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _sharedAutoFollowStop;
    private long _sharedAutoFollowGeneration;

    private void StopSharedAutoFollow(string reason)
    {
        var previous = _sharedAutoFollowStop;
        if (previous is null) return;
        _sharedAutoFollowStop = null;
        Interlocked.Increment(ref _sharedAutoFollowGeneration);
        previous.Cancel(); // Async worker owns disposal after returning.
        WorkbenchAutoFollowStatus.Text = reason;
    }

    private void WorkbenchAutoFollowStop_Click(object? sender, RoutedEventArgs e) =>
        StopSharedAutoFollow("Automatic observation OFF (explicit user stop). No remote write.");

    private async void WorkbenchAutoFollowStart_Click(object? sender, RoutedEventArgs e)
    {
        // Do not start from a port alone, a local W0 tab, or a guessed epoch.
        if (_sharedAutoFollowStop is not null)
        {
            WorkbenchAutoFollowStatus.Text = "A bounded observer is already active. Stop it before starting another.";
            return;
        }
        if (!int.TryParse(WorkbenchLedgerPort.Text, NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535 ||
            _sharedFollowHostPort != port ||
            _lastSharedHostObservation is not { Connected: true } ||
            _sharedFollowSelectedId is not string selected ||
            _sharedFollowHostInstanceId is not string epoch ||
            !Guid.TryParseExact(selected, "N", out _) ||
            !Guid.TryParseExact(epoch, "N", out _) ||
            _sharedFollowSequence is not long firstSequence)
        {
            WorkbenchAutoFollowStatus.Text =
                "Start refused. Manually read and explicitly select a host session with a valid process epoch first.";
            return;
        }
        var accessToken = Environment.GetEnvironmentVariable("SUPRACHAT_LEDGER_TOKEN");
        if (string.IsNullOrEmpty(accessToken))
        {
            WorkbenchAutoFollowStatus.Text = "Start refused: no loopback read bearer configured.";
            return;
        }
        // Fresh cancellation channel and generation prevent a late GET from
        // rewriting a manually refreshed, stopped or closed window.
        var stop = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _sharedAutoFollowGeneration);
        _sharedAutoFollowStop = stop;
        WorkbenchAutoFollowStatus.Text =
            "Automatic read-only observation ON: at most 20 sequential GETs, " +
            "minimum three seconds apart. No ChatGPT or terminal activity.";
        try
        {
            var result = await SharedSessionAutoFollow.RunAsync(
                selected, epoch, firstSequence,
                ct => SharedSessionInventoryReader.ObserveAsync(port, accessToken, ct),
                async (snapshot, projection, ct) =>
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(_sharedAutoFollowStop, stop) ||
                            generation != Interlocked.Read(ref _sharedAutoFollowGeneration) ||
                            _sharedFollowHostPort != port ||
                            !string.Equals(WorkbenchLedgerPort.Text,
                                port.ToString(CultureInfo.InvariantCulture),
                                StringComparison.Ordinal) ||
                            _sharedFollowSelectedId != selected)
                            throw new OperationCanceledException(ct);

                        _lastSharedHostObservation = snapshot;
                        _sharedFollowSequence = snapshot.Sequence;
                        _sharedFollowHostInstanceId = snapshot.HostInstanceId;
                        _updatingSharedFollowSelection = true;
                        try
                        {
                            WorkbenchHostSessionList.ItemsSource = snapshot.Sessions;
                            WorkbenchHostSessionList.SelectedItem =
                                snapshot.Sessions.FirstOrDefault(row => row.Id == selected);
                        }
                        finally { _updatingSharedFollowSelection = false; }
                        WorkbenchLedgerInventory.Text = string.Join(
                            Environment.NewLine, snapshot.Sessions.Select(row => row.DisplayLabel));
                        WorkbenchHostEvents.Text = projection.Events.Count == 0
                            ? "No new retained metadata events."
                            : string.Join(Environment.NewLine,
                                projection.Events.Select(row => row.DisplayLabel));
                        WorkbenchHostFollowStatus.Text =
                            $"Host session {selected[..8]}; sequence {projection.Sequence}; " +
                            $"{projection.Status}. Bounded observation only; historical completeness unproven.";
                        WorkbenchAutoFollowStatus.Text =
                            "Read-only follow active; one GET at a time, " +
                            "cancellable; no provider or terminal authority.";
                    });
                }, stop.Token);
            if (ReferenceEquals(_sharedAutoFollowStop, stop) &&
                generation == Interlocked.Read(ref _sharedAutoFollowGeneration))
            {
                _sharedAutoFollowStop = null;
                WorkbenchAutoFollowStatus.Text =
                    $"Automatic observation stopped: {result.Status}. " +
                    $"{result.DeliveredObservations} accepted reads. Continuous ChatGPT history NOT established.";
                if (result.Status.StartsWith("STOPPED_", StringComparison.Ordinal))
                {
                    // Fail-closed: stale session selection is never silently
                    // rebound after a gap, host replacement or refused read.
                    _sharedFollowSelectedId = null;
                    _lastSharedHostObservation = null;
                    _updatingSharedFollowSelection = true;
                    try { WorkbenchHostSessionList.SelectedItem = null; }
                    finally { _updatingSharedFollowSelection = false; }
                    WorkbenchHostEvents.Text =
                        "Observation halted; previously selected host is unverified. Read and reselect explicitly.";
                    WorkbenchHostFollowStatus.Text =
                        "Continuity not verified. Old observed host selection revoked locally.";
                }
            }
        }
        catch (Exception)
        {
            if (ReferenceEquals(_sharedAutoFollowStop, stop))
            {
                _sharedAutoFollowStop = null;
                _sharedFollowSelectedId = null;
                WorkbenchAutoFollowStatus.Text =
                    "Automatic observation failed closed. No remote write; manual verification required.";
            }
        }
        finally
        {
            if (ReferenceEquals(_sharedAutoFollowStop, stop))
                _sharedAutoFollowStop = null;
            stop.Dispose();
        }
    }
}
