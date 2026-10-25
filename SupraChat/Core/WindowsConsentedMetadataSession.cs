using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace SupraChat.Core;

internal sealed class LocalFirstPartyMetadataOptIn
{
    private readonly Guid _session;
    private readonly IntPtr _handle;
    private int _used;
    private LocalFirstPartyMetadataOptIn(Guid session, IntPtr handle) { _session = session; _handle = handle; }
    internal static LocalFirstPartyMetadataOptIn FromExplicitReview(Guid session, IntPtr handle, bool acknowledged, string phrase)
    {
        if (!acknowledged || phrase != "ALLOW METADATA" || session == Guid.Empty || handle == IntPtr.Zero)
            throw new UnauthorizedAccessException("Explicit local metadata experiment opt-in required.");
        return new(session, handle);
    }
    internal void Consume(Guid session, IntPtr handle)
    {
        if (session != _session || handle != _handle || Interlocked.CompareExchange(ref _used, 1, 0) != 0)
            throw new UnauthorizedAccessException("Metadata experiment opt-in refused.");
    }
}

public sealed record NativeMetadataStatus(MetadataConsentStatus Consent, bool ObserverAttached, bool Attaching,
    long NativeEvents, long NativeLoss, string Scope, string Completeness = "UNKNOWN", bool ProviderOperationVerified = false,
    NativeDiagnosticSnapshot? Diagnostics = null, NativeCaptureEndReason EndReason = NativeCaptureEndReason.None,
    bool CleanupComplete = false, bool EnableCompleted = false, bool DisableCompleted = false);

/// <summary>Owner UI-thread native capture. No remote enrollment, shared privileged export, payload or CDP command API.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsConsentedMetadataSession : IAsyncDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly IntPtr _handle;
    private IntPtr _identity;
    private readonly Func<IntPtr> _currentHandle;
    private readonly CoreWebView2 _core;
    private readonly Uri _origin;
    private readonly TraceSurface _surface;
    private readonly TimeProvider _time;
    private readonly TraceNormalizer _trace = new();
    private readonly LocalTraceSession _session;
    private readonly MetadataCaptureConsent _consent;
    private readonly MetadataConsentBinding _binding;
    private object _view = new();
    private MetadataConsentPlan? _currentPlan;
    private WindowsWebViewTraceAdapter? _adapter;
    private Task? _attachment;
    private ITimer? _expiry;
    private readonly DispatcherTimer _watchdog;
    private bool _navigating, _disposed, _viewInvalidated, _starting;
    private long _events, _loss;
    private NativeDiagnosticSnapshot _previousDiagnostics = new(0, 0, new Dictionary<string, long>());
    private NativeCaptureEndReason _endReason;

    private WindowsConsentedMetadataSession(IntPtr handle, Guid sessionId, Uri origin, TraceSurface surface,
        Func<IntPtr> currentHandle, TimeProvider time)
    {
        _handle = handle; _currentHandle = currentHandle; _origin = origin; _surface = surface; _time = time;
        if (sessionId == Guid.Empty) throw new ArgumentException("Authoritative session required.");
        if (new[] { "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER", "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER" }.Any(name =>
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            throw new UnauthorizedAccessException("Native metadata environment overrides refused.");
        WindowsWebViewTraceAdapter.ValidateOrigin(origin, surface);
        if (handle == IntPtr.Zero || currentHandle() != handle) throw new UnauthorizedAccessException("Owned native view unavailable.");
        _core = CoreWebView2.CreateFromComICoreWebView2(handle);
        ValidateSource();
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        if (Marshal.QueryInterface(handle, in unknown, out _identity) < 0 || _identity == IntPtr.Zero)
            throw new InvalidOperationException("Native identity unavailable.");
        _session = _trace.CreateSession(sessionId);
        _consent = new(_trace, time);
        _binding = _consent.RegisterNative(_session, _view, surface == TraceSurface.ChatGptWebView);
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _watchdog.Tick += (_, _) => { if (_disposed) return; CheckView(); RefreshEnding(); };
        try
        {
            _core.NavigationStarting += NavigationStarting;
            _core.NavigationCompleted += NavigationCompleted;
            _watchdog.Start();
        }
        catch
        {
            _watchdog.Stop();
            try { _core.NavigationStarting -= NavigationStarting; _core.NavigationCompleted -= NavigationCompleted; } catch { }
            _consent.Dispose(); _trace.Dispose();
            Marshal.Release(_identity); _identity = IntPtr.Zero;
            throw new InvalidOperationException("Native metadata review could not prepare.");
        }
    }

    public static WindowsConsentedMetadataSession PrepareSynthetic(IntPtr handle, Guid authoritativeSessionId, Uri origin,
        Func<IntPtr> currentHandle, TimeProvider? time = null) => new(handle, authoritativeSessionId, origin, TraceSurface.Synthetic, currentHandle, time ?? TimeProvider.System);

    internal static WindowsConsentedMetadataSession PrepareFirstParty(IntPtr handle, Guid authoritativeSessionId,
        Func<IntPtr> currentHandle, LocalFirstPartyMetadataOptIn optIn)
    {
        optIn.Consume(authoritativeSessionId, handle);
        return new(handle, authoritativeSessionId, new Uri("https://chatgpt.com/"), TraceSurface.ChatGptWebView, currentHandle, TimeProvider.System);
    }

    private void ThreadCheck()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Native metadata owner UI thread required.");
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private void ValidateSource()
    {
        if (!Uri.TryCreate(_core.Source, UriKind.Absolute, out var uri) || uri.Scheme != _origin.Scheme ||
            uri.IdnHost != _origin.IdnHost || uri.Port != _origin.Port || uri.UserInfo.Length != 0)
            throw new UnauthorizedAccessException("Navigate the owned view to its admitted origin first.");
    }
    private bool CheckView()
    {
        try
        {
            if (_navigating) return false;
            if (_currentHandle() != _handle) { InvalidateView(NativeCaptureEndReason.ViewReplaced); return false; }
            ValidateSource(); _viewInvalidated = false; return true;
        }
        catch { InvalidateView(NativeCaptureEndReason.OriginUnavailable); return false; }
    }
    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    { _navigating = true; InvalidateView(NativeCaptureEndReason.Navigation); }
    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) => _navigating = false;
    private void InvalidateView(NativeCaptureEndReason reason)
    {
        if (_disposed) return;
        if (_viewInvalidated) { EndCapture(reason); return; }
        _viewInvalidated = true;
        var next = new object(); _consent.ReplaceView(_binding, _view, next); _view = next;
        _expiry?.Dispose(); _expiry = null; EndCapture(reason);
    }
    public MetadataConsentPlan Preview()
    {
        ThreadCheck();
        if (!CheckView() || _starting || _attachment is not null || _consent.Status(_binding).CaptureActive)
            throw new InvalidOperationException("Stop capture and wait for the owned view before reviewing again.");
        return _consent.Preview(_binding, _surface == TraceSurface.ChatGptWebView ? MetadataConsentScope.FirstPartyMetadata : MetadataConsentScope.SyntheticMetadata);
    }
    public bool IsPending(MetadataConsentPlan plan) { ThreadCheck(); return CheckView() && _consent.IsPending(_binding, plan); }

    public async Task<bool> ConfirmAsync(MetadataConsentPlan plan, bool reviewed)
    {
        ThreadCheck();
        if (!CheckView() || _starting || _attachment is not null) return false;
        var result = _consent.Confirm(_binding, plan, reviewed);
        if (result.Outcome != "ALLOWED" || !_consent.NativeLeaseActive(_binding, _view, plan)) return false;
        if (_adapter is { IsStopped: false }) return ReferenceEquals(_currentPlan, plan);
        _currentPlan = plan;
        _endReason = NativeCaptureEndReason.None;
        var view = _view;
        bool Allowed() => !_disposed && CheckView() && _consent.NativeLeaseActive(_binding, view, plan);
        _expiry?.Dispose();
        ITimer? leaseTimer = null;
        leaseTimer = _time.CreateTimer(_ =>
        {
            // Revoke recorder ingestion independently of UI polling/traffic; COM cleanup is dispatched.
            try
            {
                var remaining = _consent.NativeLeaseRemaining(_binding, view, plan);
                if (remaining > TimeSpan.Zero) leaseTimer?.Change(remaining, Timeout.InfiniteTimeSpan); // Timer granularity can wake early.
                else Dispatcher.UIThread.Post(() => { if (!_disposed && ReferenceEquals(_currentPlan, plan)) RefreshEnding(); });
            }
            catch { /* No raw errors or provider data leave the lease timer. */ }
        }, null, _consent.NativeLeaseRemaining(_binding, view, plan), Timeout.InfiniteTimeSpan);
        _expiry = leaseTimer;
        try
        {
            _starting = true;
            _attachment = Attach();
            await _attachment;
            return Allowed() && _adapter is { IsStopped: false };
        }
        catch { if (!_disposed) { _consent.Stop(_binding); EndCapture(NativeCaptureEndReason.AttachFailure); } return false; }
        finally { _attachment = null; _starting = false; }

        async Task Attach()
        {
            if (_adapter is { } previous)
            {
                await previous.DisposeAsync();
                if (!previous.CleanupComplete) throw new InvalidOperationException("Previous native cleanup is still pending.");
                _events += previous.ObservedNetworkEvents; _loss += previous.DroppedObservations; _adapter = null;
                _previousDiagnostics = NativeCaptureDiagnostics.Merge(_previousDiagnostics, previous.Diagnostics);
            }
            if (!Allowed()) return;
            var adapter = await WindowsWebViewTraceAdapter.AttachConsentedAsync(_handle,
                _consent.NativeSink(_binding, view, plan), _session, _origin, _surface, Allowed,
                () => InvalidateView(NativeCaptureEndReason.Navigation), pending => _adapter = pending);
            if (!Allowed()) { adapter.StopObserving(); await adapter.DisposeAsync(); return; }
            _adapter = adapter;
        }
    }

    private void RefreshEnding()
    {
        if (_currentPlan is not { } plan || _endReason != NativeCaptureEndReason.None) return;
        if (!_consent.NativeLeaseActive(_binding, _view, plan))
            EndCapture(NativeCaptureDiagnostics.InactiveReason(_consent.Status(_binding).State));
        else if (!_starting && _attachment is null && _adapter is { IsStopped: true })
        {
            EndCapture(NativeCaptureEndReason.ObserverFailure);
            _consent.Stop(_binding); _expiry?.Dispose(); _expiry = null;
        }
    }
    private void StopObserver() => _adapter?.StopObserving();
    private void EndCapture(NativeCaptureEndReason reason)
    {
        if (_currentPlan is not null && _endReason == NativeCaptureEndReason.None) _endReason = reason;
        StopObserver();
    }
    public void Stop()
    {
        ThreadCheck();
        // Refresh before intentional stop so a previously expired lease keeps its first reason.
        RefreshEnding();
        _consent.Stop(_binding); _expiry?.Dispose(); _expiry = null; EndCapture(NativeCaptureEndReason.OperatorStop);
    }
    public async Task StopForSummaryAsync()
    {
        ThreadCheck();
        RefreshEnding();
        EndCapture(NativeCaptureEndReason.SummarySave); Stop();
        if (_attachment is { } attaching) { try { await attaching.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        if (_adapter is { } adapter) await adapter.DisposeAsync();
    }
    internal void StopForReviewClose()
    {
        ThreadCheck();
        RefreshEnding();
        EndCapture(NativeCaptureEndReason.ReviewClosed); Stop();
    }
    public NativeMetadataStatus Status()
    {
        ThreadCheck(); CheckView();
        RefreshEnding();
        return new(_consent.Status(_binding), _adapter is { IsStopped: false }, _starting || _attachment is not null,
            _events + (_adapter?.ObservedNetworkEvents ?? 0), _loss + (_adapter?.DroppedObservations ?? 0),
            _surface == TraceSurface.ChatGptWebView ? "SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN" : "SYNTHETIC_LOOPBACK_NATIVE",
            Diagnostics: NativeCaptureDiagnostics.Merge(_previousDiagnostics, _adapter?.Diagnostics ?? new(0, 0, new Dictionary<string, long>())),
            EndReason: _endReason, CleanupComplete: !_starting && _attachment is null && (_adapter?.CleanupComplete ?? true),
            EnableCompleted: _adapter?.EnableCompleted ?? false, DisableCompleted: _adapter?.DisableCompleted ?? false);
    }
    public TracePage Read(long after = 0, int limit = 100) { ThreadCheck(); Status(); return _trace.Read(_session, after, limit); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        ThreadCheck();
        RefreshEnding();
        EndCapture(NativeCaptureEndReason.ReviewClosed); Stop(); _disposed = true; _watchdog.Stop();
        try
        {
            try { _core.NavigationStarting -= NavigationStarting; _core.NavigationCompleted -= NavigationCompleted; } catch { _loss++; }
            if (_attachment is { } attaching) { try { await attaching.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            if (_adapter is { } adapter) await adapter.DisposeAsync();
        }
        finally
        {
            _consent.Dispose(); _trace.Dispose();
            if (_identity != IntPtr.Zero) { Marshal.Release(_identity); _identity = IntPtr.Zero; }
        }
    }
}
