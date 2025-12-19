using System.Reflection;
using System.Runtime.Loader;

namespace RemoteExec.Server;

public class RemoteJobAssemblyLoadContext(string name) : AssemblyLoadContext(name, true)
{
    public event EventHandler<RequestAssemblyEventArgs>? RequestAssembly;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        RequestAssemblyEventArgs requestAssemblyEventArgs = new RequestAssemblyEventArgs(assemblyName);

        RequestAssembly?.Invoke(this, requestAssemblyEventArgs);

        return requestAssemblyEventArgs.GetAssemblyAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }
}
