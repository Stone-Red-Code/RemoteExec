using System.Reflection;

namespace RemoteExec.Server.Utilities;

public static class AssemblyUtilities
{
    public static async Task PreLoadReferencedAssembliesAsync(RemoteJobAssemblyLoadContext assemblyLoadContext, Assembly assembly, Func<string, Task<byte[]>> requestAssembly)
    {
        AssemblyName[] referencedAssemblies = assembly.GetReferencedAssemblies();

        foreach (AssemblyName referencedAssembly in referencedAssemblies)
        {
            // Try to load from the assembly load context first
            Assembly? loadedAssembly = assemblyLoadContext.Assemblies.FirstOrDefault(a => a.GetName().FullName == referencedAssembly.FullName);

            if (loadedAssembly != null)
            {
                continue; // Already loaded in the context
            }

            // Try to load from default context (BCL assemblies)
            try
            {
                _ = assemblyLoadContext.LoadFromAssemblyName(referencedAssembly);
                continue; // Successfully loaded from default context
            }
            catch
            {
                byte[] assemblyBytes = await requestAssembly(referencedAssembly.FullName!);
                _ = assemblyLoadContext.LoadFromBytes(assemblyBytes);
            }
        }
    }

    public static Assembly LoadFromBytes(this RemoteJobAssemblyLoadContext assemblyLoadContext, byte[] assemblyBytes)
    {
        using MemoryStream ms = new(assemblyBytes);
        return assemblyLoadContext.LoadFromStream(ms);
    }
}
