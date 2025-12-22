namespace RemoteExec.Client;

internal class AsyncManualResetEvent
{
    private volatile TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsyncManualResetEvent(bool initialState)
    {
        if (initialState)
        {
            _ = _tcs.TrySetResult(true);
        }
    }

    public Task WaitAsync(CancellationToken ct)
    {
        return _tcs.Task.WaitAsync(ct);
    }

    public void Set()
    {
        _ = _tcs.TrySetResult(true);
    }

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