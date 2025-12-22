using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RemoteExec.Shared;

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace RemoteExec.Client;

public partial class RemoteExecutor : IAsyncDisposable
{
    private readonly List<ServerConnection> servers = [];
    private readonly BlockingCollection<PendingTask> globalQueue = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteExecutionResult>> pendingResults = new();
    private readonly ConcurrentDictionary<ServerConnection, ConcurrentDictionary<Guid, PendingTask>> serverAssignedTasks = new();

    private readonly AsyncManualResetEvent serverAvailableSignal = new(false);
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
            serverAssignedTasks[serverConnection] = new ConcurrentDictionary<Guid, PendingTask>();
        }
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

        if (assembly.IsDynamic)
        {
            throw new InvalidOperationException("Dynamic assemblies are not supported");
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

    protected virtual async Task DisposeAsync(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                if (!distributorCts.IsCancellationRequested)
                {
                    await StopAsync();
                }

                distributorCts.Dispose();

                foreach (ServerConnection server in servers)
                {
                    await server.Connection.DisposeAsync();
                    server.HttpClient.Dispose();
                }

                globalQueue.Dispose();
            }

            disposedValue = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(disposing: true);
        GC.SuppressFinalize(this);
    }
}