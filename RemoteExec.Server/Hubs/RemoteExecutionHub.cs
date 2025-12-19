using Microsoft.AspNetCore.SignalR;

using RemoteExec.Shared;

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Channels;

namespace RemoteExec.Server.Hubs;

public class RemoteExecutionHub(ILogger<RemoteExecutionHub> logger) : Hub
{
    private static readonly ConcurrentDictionary<string, RemoteJobAssemblyLoadContext> connections = new();

    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<byte[]>> pendingAssemblyRequests = new();

    public override Task OnConnectedAsync()
    {
        RemoteJobAssemblyLoadContext assemblyLoadContext = new RemoteJobAssemblyLoadContext($"RemoteJob_{Guid.NewGuid()}");

        assemblyLoadContext.Resolving += AssemblyLoadContext_Resolving;

        _ = connections.TryAdd(Context.ConnectionId, assemblyLoadContext);

        return base.OnConnectedAsync();
    }

    private Assembly? AssemblyLoadContext_Resolving(AssemblyLoadContext assemblyLoadContext, AssemblyName assemblyName)
    {
        logger.LogWarning("Assembly resolution triggered synchronously for {AssemblyName}. This should have been pre-loaded.", assemblyName.FullName);

        // Return null to let other resolution mechanisms try
        return null;
    }

    public async Task ProvideAssembly(Guid requestId, ChannelReader<byte> stream)
    {
        using MemoryStream ms = new MemoryStream();

        while (await stream.WaitToReadAsync())
        {
            while (stream.TryRead(out byte item))
            {
                ms.WriteByte(item);
            }
        }

        byte[] assemblyBytes = ms.ToArray();

        if (pendingAssemblyRequests.TryRemove(requestId, out TaskCompletionSource<byte[]>? tcs))
        {
            tcs.SetResult(assemblyBytes);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (connections.TryRemove(Context.ConnectionId, out RemoteJobAssemblyLoadContext? assemblyLoadContext))
        {
            assemblyLoadContext.Unload();
        }

        return base.OnDisconnectedAsync(exception);
    }

    public async Task<RemoteExecutionResult> Execute(RemoteExecutionRequest req)
    {
        try
        {
            if (!connections.TryGetValue(Context.ConnectionId, out RemoteJobAssemblyLoadContext? assemblyLoadContext))
            {
                throw new InvalidOperationException("Connection not found");
            }

            Assembly? assembly = assemblyLoadContext.Assemblies.FirstOrDefault(a => a.GetName().FullName == req.AssemblyName);

            assembly ??= await RequestAssemblyAsync(req.AssemblyName);

            if (!assemblyLoadContext.Assemblies.Contains(assembly))
            {
                using MemoryStream ms = new MemoryStream(await GetAssemblyBytesAsync(assembly));
                assembly = assemblyLoadContext.LoadFromStream(ms);
            }

            Type type = assembly.GetType(req.TypeName, throwOnError: true)!;

            Type[] argTypes = req.ArgumentTypes
                .Select(Type.GetType)
                .ToArray()!;

            MethodInfo? method = type.GetMethod(
                req.MethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                argTypes,
                modifiers: null) ?? throw new MissingMethodException(req.TypeName, req.MethodName);

            // Pre-load all referenced assemblies to avoid triggering Resolving event during Invoke
            await PreLoadReferencedAssembliesAsync(assemblyLoadContext, assembly);

            ParameterInfo[] parameters = method.GetParameters();

            if (parameters.Length != req.Arguments.Length)
            {
                throw new ArgumentException("Argument count mismatch");
            }

            object?[] invokeArgs = new object?[req.Arguments.Length];

            for (int i = 0; i < invokeArgs.Length; i++)
            {
                Type targetType = parameters[i].ParameterType;
                object arg = req.Arguments[i];

                if (arg is JsonElement je)
                {
                    // Deserialize the JSON element into the expected CLR type
                    invokeArgs[i] = JsonSerializer.Deserialize(je.GetRawText(), targetType);
                }
                else if (arg == null)
                {
                    invokeArgs[i] = null;
                }
                else if (!targetType.IsInstanceOfType(arg))
                {
                    // Fallback for simple primitive conversions
                    invokeArgs[i] = Convert.ChangeType(arg, targetType);
                }
                else
                {
                    invokeArgs[i] = arg;
                }
            }

            object? result = method.Invoke(null, invokeArgs);

            return new RemoteExecutionResult
            {
                Result = result
            };
        }
        catch (Exception ex)
        {
            return new RemoteExecutionResult
            {
                Exception = ex.ToString()
            };
        }
    }

    private async Task<Assembly> RequestAssemblyAsync(string assemblyName)
    {
        try
        {
            Guid guid = Guid.NewGuid();
            TaskCompletionSource<byte[]> tcs = new TaskCompletionSource<byte[]>();

            _ = pendingAssemblyRequests.TryAdd(guid, tcs);

            logger.LogInformation("Requesting assembly {Assembly} with request ID {RequestId}", assemblyName, guid);

            await Clients.Caller.SendAsync("RequestAssembly", assemblyName, guid);

            logger.LogInformation("Waiting for assembly {Assembly} with request ID {RequestId}", assemblyName, guid);

            // Wait for the assembly with a timeout
            byte[] assemblyBytes = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

            logger.LogInformation("Received assembly {Assembly} with request ID {RequestId}", assemblyName, guid);

            // Return a temporary assembly just for metadata inspection
            using MemoryStream ms = new MemoryStream(assemblyBytes);
            return Assembly.Load(assemblyBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error requesting assembly {Assembly}", assemblyName);
            throw;
        }
    }

    private async Task<byte[]> GetAssemblyBytesAsync(Assembly assembly)
    {
        string assemblyName = assembly.GetName().FullName!;

        Guid guid = Guid.NewGuid();
        TaskCompletionSource<byte[]> tcs = new TaskCompletionSource<byte[]>();

        _ = pendingAssemblyRequests.TryAdd(guid, tcs);

        logger.LogInformation("Requesting assembly bytes for {Assembly} with request ID {RequestId}", assemblyName, guid);

        await Clients.Caller.SendAsync("RequestAssembly", assemblyName, guid);

        byte[] assemblyBytes = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        logger.LogInformation("Received assembly bytes for {Assembly} with request ID {RequestId}", assemblyName, guid);

        return assemblyBytes;
    }

    private async Task PreLoadReferencedAssembliesAsync(RemoteJobAssemblyLoadContext assemblyLoadContext, Assembly assembly)
    {
        AssemblyName[] referencedAssemblies = assembly.GetReferencedAssemblies();

        foreach (AssemblyName referencedAssembly in referencedAssemblies)
        {
            try
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
                    // If not in default context, request from client
                    logger.LogInformation("Pre-loading referenced assembly {Assembly}", referencedAssembly.FullName);

                    Assembly tempAssembly = await RequestAssemblyAsync(referencedAssembly.FullName!);
                    byte[] assemblyBytes = await GetAssemblyBytesAsync(tempAssembly);

                    using MemoryStream ms = new MemoryStream(assemblyBytes);
                    _ = assemblyLoadContext.LoadFromStream(ms);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not pre-load referenced assembly {Assembly}", referencedAssembly.FullName);
            }
        }
    }
}