using Microsoft.AspNetCore.SignalR;

using RemoteExec.Shared;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace RemoteExec.Server.Hubs;

public class RemoteExecutionHub(ILogger<RemoteExecutionHub> logger) : Hub
{
    private static readonly ConcurrentDictionary<string, RemoteJobAssemblyLoadContext> connections = new();

    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<byte[]>> pendingAssemblyRequests = new();

    private static ServerMetrics? lastMetrics;
    private static DateTime lastMetricsTimestamp;
    private static TimeSpan lastTotalProcessorTime;

    private static int activeTasks = 0;
    private static readonly int maxConcurrentTasks = Environment.ProcessorCount * 2;
    private static readonly SemaphoreSlim taskSemaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);

    public override Task OnConnectedAsync()
    {
        RemoteJobAssemblyLoadContext assemblyLoadContext = new RemoteJobAssemblyLoadContext($"RemoteJob_{Guid.NewGuid()}");

        _ = connections.TryAdd(Context.ConnectionId, assemblyLoadContext);

        logger.LogInformation("Connection {ConnectionId} established", Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (connections.TryRemove(Context.ConnectionId, out RemoteJobAssemblyLoadContext? assemblyLoadContext))
        {
            assemblyLoadContext.Unload();
            logger.LogInformation("Connection {ConnectionId} disconnected", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    public async Task StartTaskStream(IAsyncEnumerable<TaskItem> taskStream)
    {
        logger.LogInformation("Starting task stream for connection {ConnectionId}", Context.ConnectionId);

        try
        {
            await foreach (TaskItem taskItem in taskStream.WithCancellation(Context.ConnectionAborted))
            {
                // Wait for available slot before processing
                await taskSemaphore.WaitAsync(Context.ConnectionAborted);

                // Process task asynchronously without blocking the stream
                _ = Task.Run(async () =>
                {
                    try
                    {
                        RemoteExecutionResult result = await ExecuteTask(taskItem.Request);
                        await Clients.Caller.SendAsync("TaskResult", taskItem.TaskId, result);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing task {TaskId}", taskItem.TaskId);
                        RemoteExecutionResult errorResult = new RemoteExecutionResult
                        {
                            Exception = ex.ToString()
                        };
                        await Clients.Caller.SendAsync("TaskResult", taskItem.TaskId, errorResult);
                    }
                    finally
                    {
                        _ = taskSemaphore.Release();
                    }
                }, Context.ConnectionAborted);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Task stream for connection {ConnectionId} was canceled", Context.ConnectionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in task stream for connection {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task<RemoteExecutionResult> Execute(RemoteExecutionRequest req)
    {
        return await ExecuteTask(req);
    }

    private async Task<RemoteExecutionResult> ExecuteTask(RemoteExecutionRequest req)
    {
        _ = Interlocked.Increment(ref activeTasks);

        logger.LogInformation(req.MethodName);

        try
        {
            if (!connections.TryGetValue(Context.ConnectionId, out RemoteJobAssemblyLoadContext? assemblyLoadContext))
            {
                logger.LogError("Connection {ConnectionId} not found", Context.ConnectionId);
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
                logger.LogError("Argument count mismatch for method {Method} in type {Type} for connection {ConnectionId}", req.MethodName, req.TypeName, Context.ConnectionId);
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

            if (result is Task taskResult)
            {
                await taskResult.ConfigureAwait(false);
                Type returnType = method.ReturnType;
                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    // For Task<T>, get the Result property
                    PropertyInfo resultProperty = returnType.GetProperty("Result")!;
                    result = resultProperty.GetValue(taskResult);
                }
                else
                {
                    // For non-generic Task, result is null
                    result = null;
                }
            }

            return new RemoteExecutionResult
            {
                Result = result
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing remote method {Method} in type {Type} for connection {ConnectionId}", req.MethodName, req.TypeName, Context.ConnectionId);

            return new RemoteExecutionResult
            {
                Exception = ex.ToString()
            };
        }
        finally
        {
            _ = Interlocked.Decrement(ref activeTasks);
        }
    }

    public static async Task ProvideAssembly(Guid requestId, byte[] assemblyBytes)
    {
        if (pendingAssemblyRequests.TryRemove(requestId, out TaskCompletionSource<byte[]>? tcs))
        {
            tcs.SetResult(assemblyBytes);
        }
    }

    public async Task<ServerMetrics> GetMetrics()
    {
        return await GetServerMetrics();
    }

    public static async Task BroadcastMetricsAsync(IHubContext<RemoteExecutionHub> hubContext)
    {
        ServerMetrics metrics = await GetServerMetrics();

        // Only broadcast if metrics have changed by a significant amount
        if (lastMetrics is not null)
        {
            double cpuDiff = Math.Abs(metrics.CpuUsage - lastMetrics.CpuUsage);
            long memoryDiff = Math.Abs(metrics.TotalMemoryUsage - lastMetrics.TotalMemoryUsage);
            int connectionsDiff = Math.Abs(metrics.ActiveConnections - lastMetrics.ActiveConnections);
            int tasksDiff = Math.Abs(metrics.ActiveTasks - lastMetrics.ActiveTasks);
            int maxTasksDiff = Math.Abs(metrics.MaxConcurrentTasks - lastMetrics.MaxConcurrentTasks);

            if (cpuDiff < 1.0 && memoryDiff < 10 * 1024 * 1024 && connectionsDiff == 0 && tasksDiff == 0 && maxTasksDiff == 0)
            {
                return; // No significant change
            }
        }

        lastMetrics = metrics;

        await hubContext.Clients.All.SendAsync("MetricsUpdated", metrics);
    }

    private static async Task<ServerMetrics> GetServerMetrics()
    {
        Process currentProcess = Process.GetCurrentProcess();

        // Capture current values
        DateTime currentTime = DateTime.UtcNow;
        TimeSpan currentProcessorTime = currentProcess.TotalProcessorTime;

        // Calculate the difference since the last check
        double elapsedMs = (currentTime - lastMetricsTimestamp).TotalMilliseconds;
        double cpuMsUsed = (currentProcessorTime - lastTotalProcessorTime).TotalMilliseconds;

        // Calculate percentage: (Time Used / Time Elapsed) / Cores
        // We multiply by 100 to get a 0-100 scale
        double cpuUsagePercent = cpuMsUsed / elapsedMs / Environment.ProcessorCount * 100;

        // Update static variables for the next call
        lastMetricsTimestamp = currentTime;
        lastTotalProcessorTime = currentProcessorTime;

        return new ServerMetrics
        {
            ServerId = Environment.MachineName,
            ActiveConnections = connections.Count,
            ActiveTasks = activeTasks,
            MaxConcurrentTasks = maxConcurrentTasks,
            TotalMemoryUsage = currentProcess.WorkingSet64,
            CpuUsage = Math.Clamp(Math.Round(cpuUsagePercent, 2), 0, 100),
            Timestamp = currentTime
        };
    }

    private async Task<Assembly> RequestAssemblyAsync(string assemblyName)
    {
        try
        {
            Guid guid = Guid.NewGuid();
            TaskCompletionSource<byte[]> tcs = new TaskCompletionSource<byte[]>();

            _ = pendingAssemblyRequests.TryAdd(guid, tcs);

            await Clients.Caller.SendAsync("RequestAssembly", assemblyName, guid);

            // Wait for the assembly with a timeout
            byte[] assemblyBytes = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

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

        await Clients.Caller.SendAsync("RequestAssembly", assemblyName, guid);

        byte[] assemblyBytes = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

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