namespace SupraChat.Core;

/// <summary>Keeps cleanup behind synchronous COM registration and enable-task publication.</summary>
internal sealed class NativeNetworkRegistration
{
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _enable;
    internal void Publish(Task enable)
    {
        if (_finished.Task.IsCompleted) throw new InvalidOperationException("Native registration already completed.");
        _enable = enable;
    }
    internal void Complete() => _finished.TrySetResult();
    internal async Task WaitForEnableAsync()
    {
        await _finished.Task;
        if (_enable is not null) await _enable;
    }
}
