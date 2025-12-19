using System.Runtime.Loader;

namespace RemoteExec.Server;

public class RemoteJobAssemblyLoadContext(string name) : AssemblyLoadContext(name, true)
{
}
