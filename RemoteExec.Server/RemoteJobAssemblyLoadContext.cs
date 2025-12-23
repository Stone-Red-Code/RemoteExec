using System.Runtime.Loader;

namespace RemoteExec.Server;

/// <summary>
/// An isolated assembly load context for remote job execution, allowing assemblies to be unloaded.
/// </summary>
public class RemoteJobAssemblyLoadContext(string name) : AssemblyLoadContext(name, true)
{
}
