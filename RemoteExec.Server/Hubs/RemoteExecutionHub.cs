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

    // Track pending assembly requests per connection to avoid duplicate requests
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Lazy<Task<Assembly>>>> pendingAssemblyRequestsByConnection = new();

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
        _ = pendingAssemblyRequestsByConnection.TryAdd(Context.ConnectionId, new ConcurrentDictionary<string, Lazy<Task<Assembly>>>());

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

        _ = pendingAssemblyRequestsByConnection.TryRemove(Context.ConnectionId, out _);

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

        try
        {
            if (!connections.TryGetValue(Context.ConnectionId, out RemoteJobAssemblyLoadContext? assemblyLoadContext))
            {
                logger.LogError("Connection {ConnectionId} not found", Context.ConnectionId);
                throw new InvalidOperationException("Connection not found");
            }

            // Check if assembly is already loaded in the context
            Assembly? assembly = assemblyLoadContext.Assemblies.FirstOrDefault(a => a.GetName().FullName == req.AssemblyName);

            // If not loaded, request and load it into the context
            assembly ??= await LoadAssemblyAsync(req.AssemblyName, assemblyLoadContext);

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

        DateTime currentTime = DateTime.UtcNow;
        TimeSpan currentProcessorTime = currentProcess.TotalProcessorTime;

        double elapsedMs = (currentTime - lastMetricsTimestamp).TotalMilliseconds;
        double cpuMsUsed = (currentProcessorTime - lastTotalProcessorTime).TotalMilliseconds;

        double cpuUsagePercent = cpuMsUsed / elapsedMs / Environment.ProcessorCount * 100;

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

    private async Task<Assembly> LoadAssemblyAsync(string assemblyName, RemoteJobAssemblyLoadContext assemblyLoadContext)
    {
        try
        {
            if (!pendingAssemblyRequestsByConnection.TryGetValue(Context.ConnectionId, out ConcurrentDictionary<string, Lazy<Task<Assembly>>>? connectionPendingRequests))
            {
                throw new InvalidOperationException("Connection not found");
            }

            // Use Lazy<Task<T>> pattern to ensure only one request is made
            // The Lazy.Value is only evaluated once, even if multiple threads access it simultaneously
            Lazy<Task<Assembly>> lazyTask = connectionPendingRequests.GetOrAdd(assemblyName, key =>
            {
                return new Lazy<Task<Assembly>>(() => Task.Run(async () =>
                {
                    try
                    {
                        Guid guid = Guid.NewGuid();
                        TaskCompletionSource<byte[]> tcs = new TaskCompletionSource<byte[]>();

                        _ = pendingAssemblyRequests.TryAdd(guid, tcs);

                        await Clients.Caller.SendAsync("RequestAssembly", key, guid);

                        byte[] assemblyBytes = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

                        using MemoryStream ms = new MemoryStream(assemblyBytes);
                        return assemblyLoadContext.LoadFromStream(ms);
                    }
                    finally
                    {
                        _ = connectionPendingRequests.TryRemove(key, out _);
                    }
                }));
            });

            // All callers will await the same Task
            return await lazyTask.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading assembly {Assembly}", assemblyName);
            throw;
        }
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
                    _ = await LoadAssemblyAsync(referencedAssembly.FullName!, assemblyLoadContext);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not pre-load referenced assembly {Assembly}", referencedAssembly.FullName);
            }
        }
    }
}