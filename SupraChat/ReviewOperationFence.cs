namespace SupraChat;

/// <summary>Serializes asynchronous review transitions; stop/close invalidates suspended continuations.</summary>
internal sealed class ReviewOperationFence
{
    private readonly object _gate = new();
    private bool _busy, _closed;
    private long _generation;
    public long? TryBegin() { lock (_gate) { if (_busy || _closed) return null; _busy = true; return ++_generation; } }
    public bool Current(long generation) { lock (_gate) return _busy && !_closed && generation == _generation; }
    public void Cancel() { lock (_gate) _generation++; }
    public void Close() { lock (_gate) { _closed = true; _generation++; } }
    public void End() { lock (_gate) _busy = false; }
}
