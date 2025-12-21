using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RemoteExec.Shared;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

namespace RemoteExec.Client;

public class RemoteExecutor : IDisposable
{
    private readonly List<ServerConnection> servers = [];
    private readonly BlockingCollection<PendingTask> globalQueue = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteExecutionResult>> pendingResults = new();
    private CancellationTokenSource distributorCts = new();
    private Task? distributorTask;
    private readonly LoadBalancingStrategy loadBalancingStrategy;
    private readonly ILogger logger;
    private bool disposedValue;

    public event EventHandler<ServerMetricsUpdatedEventArgs>? MetricsUpdated;

    public RemoteExecutor(string url) : this([url], LoadBalancingStrategy.ResourceAware, NullLogger.Instance)
    {
    }

    public RemoteExecutor(string url, ILogger logger) : this([url], LoadBalancingStrategy.ResourceAware, logger)
    {
    }

    public RemoteExecutor(string url, LoadBalancingStrategy loadBalancingStrategy) : this([url], loadBalancingStrategy, NullLogger.Instance)
    {
    }

    public RemoteExecutor(string[] urls) : this(urls, LoadBalancingStrategy.ResourceAware, NullLogger.Instance)
    {
    }

    public RemoteExecutor(string[] urls, LoadBalancingStrategy loadBalancingStrategy) : this(urls, loadBalancingStrategy, NullLogger.Instance)
    {
    }

    public RemoteExecutor(string[] urls, LoadBalancingStrategy loadBalancingStrategy, ILogger logger)
    {
        this.loadBalancingStrategy = loadBalancingStrategy;
        this.logger = logger;

        foreach (string url in urls)
        {
            Uri baseUri = new(url);
            Uri signalRUri = new(baseUri, "/remote");

            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(signalRUri)
                .WithAutomaticReconnect()
                .ConfigureLogging(logging =>
                {
                    _ = logging.AddProvider(new RemoteExecLoggerProvider(logger));
                })
                .Build();

            HttpClient httpClient = new()
            {
                BaseAddress = baseUri
            };

            ServerConnection serverConnection = new ServerConnection(connection, httpClient);
            servers.Add(serverConnection);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        distributorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        List<Task> startTasks = [];

        foreach (ServerConnection server in servers)
        {
            _ = server.Connection.On<ServerMetrics>("MetricsUpdated", metrics =>
            {
                server.Metrics = metrics;
                MetricsUpdated?.Invoke(this, new ServerMetricsUpdatedEventArgs(server.Connection, metrics));
            });

            _ = server.Connection.On<Guid, RemoteExecutionResult>("TaskResult", (taskId, result) =>
            {
                if (pendingResults.TryRemove(taskId, out TaskCompletionSource<RemoteExecutionResult>? tcs))
                {
                    tcs.SetResult(result);
                }
            });

            _ = server.Connection.On($"RequestAssembly", async (string assemblyName, Guid requestId) =>
            {
                Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().FullName == assemblyName) ?? Assembly.Load(new AssemblyName(assemblyName));
                byte[] dllBytes = await File.ReadAllBytesAsync(assembly.Location!);

                ByteArrayContent content = new(dllBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                _ = await server.HttpClient.PostAsync($"/provide-assembly?requestId={requestId}", content);
            });

            startTasks.Add(server.Connection.StartAsync(cancellationToken)
                .ContinueWith(async (task, state) =>
                {
                    ServerConnection conn = (ServerConnection)state!;
                    conn.Metrics = await conn.Connection.InvokeAsync<ServerMetrics>("GetMetrics", cancellationToken);
                    MetricsUpdated?.Invoke(this, new ServerMetricsUpdatedEventArgs(conn.Connection, conn.Metrics));

                    await conn.Connection.SendAsync("StartTaskStream", conn.TaskChannel.Reader, cancellationToken);
                }, server, TaskScheduler.Default).Unwrap());
        }

        await Task.WhenAll(startTasks);

        distributorTask = Task.Run(() => DistributorLoop(distributorCts.Token), distributorCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Stopping RemoteExecutor...");

        await distributorCts.CancelAsync();

        if (distributorTask != null)
        {
            try
            {
                await distributorTask;
                logger.LogDebug("Distributor task completed successfully");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogError(ex, "Distributor task was canceled");
            }
        }

        logger.LogDebug("Completing task channels for {ServerCount} servers", servers.Count);
        foreach (ServerConnection server in servers)
        {
            server.TaskChannel.Writer.Complete();
        }

        List<Task> stopTasks = [];

        foreach (ServerConnection server in servers)
        {
            stopTasks.Add(server.Connection.StopAsync(cancellationToken));
        }

        await Task.WhenAll(stopTasks);
        logger.LogInformation("RemoteExecutor stopped successfully");
    }

    public Dictionary<string, ServerMetrics> GetCurrentServerMetrics()
    {
        return servers
            .Select(server => server.Metrics)
            .Where(metrics => metrics != null)
            .ToDictionary(metrics => metrics!.ServerId, metrics => metrics!);
    }

    public async Task<TResult> Execute<TDelegate, TResult>(TDelegate del, params object[] args) where TDelegate : Delegate
    {
        object? execResult = await Execute(del, args);

        if (execResult is TResult typedResult)
        {
            return typedResult;
        }
        else if (execResult is JsonElement jsonElement)
        {
            return jsonElement.Deserialize<TResult>()!;
        }
        else
        {
            throw new InvalidCastException("The result cannot be cast to the specified type.");
        }
    }

    public async Task<object?> Execute<T>(T del, params object[] args) where T : Delegate
    {
        MethodInfo method = del.Method;
        Type declaringType = method.DeclaringType!;
        Assembly assembly = declaringType.Assembly;

        if (!method.IsStatic)
        {
            throw new InvalidOperationException("Only static methods supported");
        }

        RemoteExecutionRequest request = new RemoteExecutionRequest
        {
            AssemblyName = assembly.GetName().FullName,
            TypeName = declaringType.FullName!,
            MethodName = method.Name,
            ArgumentTypes = [.. method.GetParameters().Select(p => p.ParameterType.AssemblyQualifiedName!)],
            Arguments = args
        };

        Guid taskId = Guid.NewGuid();
        TaskCompletionSource<RemoteExecutionResult> tcs = new();
        pendingResults[taskId] = tcs;

        PendingTask pendingTask = new PendingTask
        {
            TaskId = taskId,
            Request = request,
            EnqueuedAt = DateTime.UtcNow
        };

        globalQueue.Add(pendingTask);

        RemoteExecutionResult result = await tcs.Task;

        if (result.Exception != null)
        {
            throw new RemoteExecutionException(result.Exception);
        }

        return result.Result;
    }

    private async Task DistributorLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check if there are tasks in the global queue
                PendingTask pendingTask = globalQueue.Take(cancellationToken);

                // Select the best server based on metrics
                ServerConnection? bestServer = SelectBestServer();

                if (bestServer != null)
                {
                    // Create task item with ID
                    TaskItem taskItem = new TaskItem
                    {
                        TaskId = pendingTask.TaskId,
                        Request = pendingTask.Request
                    };

                    // Push to server's channel - SignalR will stream it
                    await bestServer.TaskChannel.Writer.WriteAsync(taskItem, cancellationToken);
                }
                else
                {
                    // No available server, re-enqueue
                    globalQueue.Add(pendingTask, cancellationToken);
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DistributorLoop exception: {ex}");
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private ServerConnection? SelectBestServer()
    {
        if (servers.Count == 0)
        {
            return null;
        }

        return loadBalancingStrategy switch
        {
            LoadBalancingStrategy.ResourceAware => servers.MinBy(s =>
            {
                if (s.Metrics == null)
                {
                    return double.MaxValue;
                }

                double cpuScore = s.Metrics.CpuUsage;
                double activeTaskScore = s.Metrics.ActiveTasks * 10;
                double backlogScore = s.TaskChannel.Reader.Count * 50;

                return cpuScore + activeTaskScore + backlogScore;
            }),
            LoadBalancingStrategy.LeastBacklog => servers.MinBy(s => s.TaskChannel.Reader.Count),

            _ => servers.FirstOrDefault()
        };
    }

    private sealed class ServerConnection(HubConnection connection, HttpClient httpClient)
    {
        public HubConnection Connection { get; } = connection;
        public HttpClient HttpClient { get; } = httpClient;
        public Channel<TaskItem> TaskChannel { get; } = Channel.CreateUnbounded<TaskItem>();
        public ServerMetrics? Metrics { get; set; }
    }

    private sealed class PendingTask
    {
        public required Guid TaskId { get; init; }
        public required RemoteExecutionRequest Request { get; init; }
        public required DateTime EnqueuedAt { get; init; }
    }

    // S2930: Dispose distributorCts when no longer needed
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                distributorCts.Dispose();
                // Dispose managed state (managed objects) here if needed
            }

            // Free unmanaged resources (unmanaged objects) and override finalizer if needed
            // Set large fields to null if needed

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
