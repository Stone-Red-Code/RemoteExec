using System.Reflection;

namespace RemoteExec.Server;

public class RequestAssemblyEventArgs(AssemblyName assemblyName) : EventArgs
{
    private readonly TaskCompletionSource taskCompletionSource = new TaskCompletionSource();

    private Assembly? assembly = null;

    public AssemblyName Assembly { get; } = assemblyName;

    public async Task<Assembly?> GetAssemblyAsync()
    {
        await taskCompletionSource.Task;
        return assembly;
    }

    public void SetAssembly(Assembly assembly)
    {
        this.assembly = assembly;
        taskCompletionSource.SetResult();
    }
}
