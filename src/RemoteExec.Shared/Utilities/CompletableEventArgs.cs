namespace RemoteExec.Shared.Utilities;

public class CompletableEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetCompleted()
    {
        _ = tcs.TrySetResult(true);
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }
        return tcs.Task;
    }
}

public class CompletableEventArgs<T> : EventArgs
{
    private readonly TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void SetCompleted(T result)
    {
        _ = tcs.TrySetResult(result);
    }
    public Task<T> WaitAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }
        return tcs.Task;
    }
}

public class CompletableEventArgs<TValue, TResult>(TValue value)
{
    public TValue Value { get; } = value;

    private readonly TaskCompletionSource<TResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void SetCompleted(TResult result)
    {
        _ = tcs.TrySetResult(result);
    }
    public Task<TResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }
        return tcs.Task;
    }
}

