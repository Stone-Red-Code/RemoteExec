using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

using RemoteExec.Server.Configuration;
using RemoteExec.Server.Services;
using RemoteExec.Shared.Models;

using System.Collections.Concurrent;
using System.Diagnostics;

namespace RemoteExec.Server.Hubs;

/// <summary>
/// SignalR hub that handles remote method execution requests from clients.
/// </summary>
public class RemoteExecutionHub : Hub
{
    private static readonly ConcurrentDictionary<string, ExecutionEnvironment> connections = new();

    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<byte[]>> pendingAssemblyRequests = new();

    // Track pending assembly requests per connection to avoid duplicate requests
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Lazy<Task<byte[]>>>> pendingAssemblyRequestsByConnection = new();

    private static ServerMetrics? lastMetrics;
    private static DateTime lastMetricsTimestamp;
    private static TimeSpan lastTotalProcessorTime;

    private static int activeTasks = 0;
    private static int maxConcurrentTasks;
    private static SemaphoreSlim taskSemaphore = null!;

    private static string? executionEnvironmentName;
    private static int assemblyLoadTimeoutSeconds;
    private static double cpuDifferenceThreshold;
    private static long memoryDifferenceThreshold;

    private readonly ILogger<RemoteExecutionHub> logger;
    private readonly IEnumerable<ExecutionEnvironment> executionEnvironments;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteExecutionHub"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="executionOptions">The execution configuration options.</param>
    /// <param name="metricsOptions">The metrics configuration options.</param>
    /// <param name="executionEnvironments">The available execution environments.</param>
    public RemoteExecutionHub(ILogger<RemoteExecutionHub> logger, IOptions<ExecutionConfiguration> executionOptions, IOptions<MetricsConfiguration> metricsOptions, IEnumerable<ExecutionEnvironment> executionEnvironments)
    {
        this.logger = logger;
        this.executionEnvironments = executionEnvironments;

        // Initialize static configuration values once
        if (taskSemaphore is null)
        {
            maxConcurrentTasks = executionOptions.Value.MaxConcurrentTasks ?? (Environment.ProcessorCount * 2);
            taskSemaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
            executionEnvironmentName = executionOptions.Value.ExecutionEnvironment;
            assemblyLoadTimeoutSeconds = executionOptions.Value.AssemblyLoadTimeoutSeconds;
            cpuDifferenceThreshold = metricsOptions.Value.CpuDifferenceThreshold;
            memoryDifferenceThreshold = metricsOptions.Value.MemoryDifferenceThreshold;
        }
    }

    /// <inheritdoc/>
    public override async Task OnConnectedAsync()
    {
        ExecutionEnvironment? executionEnvironment = executionEnvironments.FirstOrDefault(env => env.Name.Equals(executionEnvironmentName, StringComparison.OrdinalIgnoreCase));

        if (executionEnvironment is null)
        {
            logger.LogError("Execution environment '{ExecutionEnvironment}' not found for connection {ConnectionId}", executionEnvironmentName, Context.ConnectionId);
            throw new InvalidOperationException($"Execution environment '{executionEnvironmentName}' not found");
        }

        // Capture Context and Clients to avoid accessing disposed Hub instance
        HubCallerContext capturedContext = Context;
        IHubCallerClients capturedClients = Clients;

        executionEnvironment.RequestAssembly += async (sender, e) =>
        {
            byte[] assemblyBytes = await RequestAssemblyBytesAsync(e.Value, capturedContext, capturedClients);
            e.SetCompleted(assemblyBytes);
        };

        await executionEnvironment.PrepareEnvironmentAsync(Context.ConnectionAborted);

        _ = connections.TryAdd(Context.ConnectionId, executionEnvironment);
        _ = pendingAssemblyRequestsByConnection.TryAdd(Context.ConnectionId, new());

        logger.LogInformation("Connection {ConnectionId} established", Context.ConnectionId);
    }

    /// <inheritdoc/>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (connections.TryRemove(Context.ConnectionId, out ExecutionEnvironment? executionEnvironment))
        {
            await executionEnvironment.CleanupEnvironmentAsync(CancellationToken.None);
            logger.LogInformation("Connection {ConnectionId} disconnected", Context.ConnectionId);
        }

        _ = pendingAssemblyRequestsByConnection.TryRemove(Context.ConnectionId, out _);
    }

    /// <summary>
    /// Starts processing a stream of tasks from the client.
    /// </summary>
    /// <param name="taskStream">The async enumerable stream of tasks to execute.</param>
    public async Task StartTaskStream(IAsyncEnumerable<TaskItem> taskStream)
    {
        logger.LogInformation("Starting task stream for connection {ConnectionId}", Context.ConnectionId);

        try
        {
            await foreach (TaskItem taskItem in taskStream.WithCancellation(Context.ConnectionAborted))
            {
                await taskSemaphore.WaitAsync(Context.ConnectionAborted);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        RemoteExecutionResult result = await ExecuteTask(taskItem.Request);
                        await Clients.Caller.SendAsync("TaskResult", taskItem.TaskId, result);
                    }
                    catch (ObjectDisposedException ex)
                    {
                        logger.LogWarning(ex, "Connection {ConnectionId} disposed while processing task {TaskId}", Context.ConnectionId, taskItem.TaskId);
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
            logger.LogInformation(ex, "Task stream for connection {ConnectionId} was canceled", Context.ConnectionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in task stream for connection {ConnectionId}", Context.ConnectionId);
        }
    }

    private async Task<RemoteExecutionResult> ExecuteTask(RemoteExecutionRequest request)
    {
        _ = Interlocked.Increment(ref activeTasks);

        try
        {
            if (!connections.TryGetValue(Context.ConnectionId, out ExecutionEnvironment? executionEnvironment))
            {
                logger.LogError("Connection {ConnectionId} not found for executing method {Method} in type {Type}", Context.ConnectionId, request.MethodName, request.TypeName);
                throw new InvalidOperationException("Connection not found");
            }

            return await executionEnvironment.ExecuteTaskAsync(request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing remote method {Method} in type {Type} for connection {ConnectionId}", request.MethodName, request.TypeName, Context.ConnectionId);

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

    internal static async Task ProvideAssembly(Guid requestId, byte[] assemblyBytes)
    {
        if (pendingAssemblyRequests.TryRemove(requestId, out TaskCompletionSource<byte[]>? tcs))
        {
            tcs.SetResult(assemblyBytes);
        }
    }

    /// <summary>
    /// Gets the current server metrics.
    /// </summary>
    /// <returns>The current server metrics.</returns>
    public async Task<ServerMetrics> GetMetrics()
    {
        return await GetServerMetrics();
    }

    /// <summary>
    /// Broadcasts server metrics to all connected clients if metrics have changed significantly.
    /// </summary>
    /// <param name="hubContext">The hub context for broadcasting.</param>
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

            if (cpuDiff < cpuDifferenceThreshold && memoryDiff < memoryDifferenceThreshold && connectionsDiff == 0 && tasksDiff == 0 && maxTasksDiff == 0)
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

    private async Task<byte[]> RequestAssemblyBytesAsync(string assemblyName, HubCallerContext context, IHubCallerClients hubCallerClients)
    {
        try
        {
            if (!pendingAssemblyRequestsByConnection.TryGetValue(context.ConnectionId, out ConcurrentDictionary<string, Lazy<Task<byte[]>>>? connectionPendingRequests))
            {
                throw new InvalidOperationException("Connection not found");
            }

            // Use Lazy<Task<T>> pattern to ensure only one request is made
            // The Lazy.Value is only evaluated once, even if multiple threads access it simultaneously
            Lazy<Task<byte[]>> lazyTask = connectionPendingRequests.GetOrAdd(assemblyName, key =>
            {
                return new Lazy<Task<byte[]>>(() => Task.Run(async () =>
                {
                    try
                    {
                        Guid guid = Guid.NewGuid();
                        TaskCompletionSource<byte[]> tcs = new TaskCompletionSource<byte[]>();

                        _ = pendingAssemblyRequests.TryAdd(guid, tcs);

                        logger.LogInformation("Requesting assembly {Assembly} with RequestId {RequestId} for connection {ConnectionId}", key, guid, context.ConnectionId);

                        await hubCallerClients.Caller.SendAsync("RequestAssembly", key, guid);

                        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(assemblyLoadTimeoutSeconds));
                    }
                    finally
                    {
                        _ = Task.Run(async () =>
                        {
                            // WORKAROUND: Delay to avoid race condition where the same assembly is requested again because the assembly hasn't been loaded yet
                            // An alternative would be to only remove the request after the client disconnects or after a longer timeout
                            await Task.Delay(1000);
                            _ = connectionPendingRequests.TryRemove(key, out _);
                        });
                    }
                }));
            });

            // All callers will await the same Task
            return await lazyTask.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading assembly {Assembly}", assemblyName);
        }

        return [];
    }
}