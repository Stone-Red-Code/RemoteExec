using RemoteExec.Server.Utilities;
using RemoteExec.Shared;

namespace RemoteExec.Server.Services;

public abstract class ExecutionEnvironment
{
    public event EventHandler<CompletableEventArgs<string, byte[]>>? RequestAssembly;

    public abstract string Name { get; }

    public abstract Task PrepareEnvironmentAsync(CancellationToken cancellationToken);

    public abstract Task<RemoteExecutionResult> ExecuteTaskAsync(RemoteExecutionRequest request);

    public abstract Task CleanupEnvironmentAsync(CancellationToken cancellationToken);

    protected async Task<byte[]> RequestAssemblyAsync(string assemblyName)
    {
        CompletableEventArgs<string, byte[]> args = new CompletableEventArgs<string, byte[]>(assemblyName);
        RequestAssembly?.Invoke(this, args);
        return await args.WaitAsync();
    }
}
