namespace RemoteExec.Client;

/// <summary>
/// An async-compatible manual reset event that can be awaited.
/// </summary>
internal class AsyncManualResetEvent
{
    private volatile TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncManualResetEvent"/> class.
    /// </summary>
    /// <param name="initialState">If true, the event is initially signaled.</param>
    public AsyncManualResetEvent(bool initialState)
    {
        if (initialState)
        {
            _ = _tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Asynchronously waits for the event to be signaled.
    /// </summary>
    /// <param name="ct">A cancellation token to cancel the wait operation.</param>
    /// <returns>A task that completes when the event is signaled.</returns>
    public Task WaitAsync(CancellationToken ct)
    {
        return _tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Sets the event to a signaled state, releasing all waiting tasks.
    /// </summary>
    public void Set()
    {
        _ = _tcs.TrySetResult(true);
    }

    /// <summary>
    /// Resets the event to an unsignaled state.
    /// </summary>
    public void Reset()
    {
        while (true)
        {
            TaskCompletionSource<bool> tcs = _tcs;
            if (!tcs.Task.IsCompleted ||
                Interlocked.CompareExchange(ref _tcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), tcs) == tcs)
            {
                return;
            }
        }
    }
}