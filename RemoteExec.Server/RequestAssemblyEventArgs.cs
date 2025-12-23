using System.Reflection;

namespace RemoteExec.Server;

/// <summary>
/// Event arguments for assembly request events.
/// </summary>
public class RequestAssemblyEventArgs(AssemblyName assemblyName) : EventArgs
{
    private readonly TaskCompletionSource taskCompletionSource = new TaskCompletionSource();

    private Assembly? assembly = null;

    /// <summary>
    /// Gets the assembly name being requested.
    /// </summary>
    public AssemblyName Assembly { get; } = assemblyName;

    /// <summary>
    /// Asynchronously waits for the assembly to be provided.
    /// </summary>
    /// <returns>The assembly, or null if not provided.</returns>
    public async Task<Assembly?> GetAssemblyAsync()
    {
        await taskCompletionSource.Task;
        return assembly;
    }

    /// <summary>
    /// Sets the assembly to fulfill the request.
    /// </summary>
    /// <param name="assembly">The assembly to provide.</param>
    public void SetAssembly(Assembly assembly)
    {
        this.assembly = assembly;
        taskCompletionSource.SetResult();
    }
}
