using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RemoteExec.Client.Exceptions;
using RemoteExec.Shared;

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace RemoteExec.Client;

public partial class RemoteExecutor : IAsyncDisposable
{
    private readonly BlockingCollection<PendingTask> globalQueue;
    private readonly List<ServerConnection> servers = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteExecutionResult>> pendingResults = new();
    private readonly ConcurrentDictionary<ServerConnection, ConcurrentDictionary<Guid, PendingTask>> serverAssignedTasks = new();

    private readonly AsyncManualResetEvent serverAvailableSignal = new(false);
    private CancellationTokenSource distributorCts = new();
    private Task? distributorTask;

    private readonly RemoteExecutorOptions options = new();
    private readonly ILogger logger;

    private bool disposedValue;

    public event EventHandler<ServerMetricsUpdatedEventArgs>? MetricsUpdated;

    public RemoteExecutor(string url) : this([url], new RemoteExecutorOptions(), NullLogger.Instance)
    {
    }

    public RemoteExecutor(string url, ILogger logger) : this([url], new RemoteExecutorOptions(), logger)
    {
    }

    public RemoteExecutor(string url, Action<RemoteExecutorOptions> configure) : this([url], NullLogger.Instance, configure)
    {
    }

    public RemoteExecutor(string url, ILogger logger, Action<RemoteExecutorOptions> configure) : this([url], logger, configure)
    {
    }

    public RemoteExecutor(string[] urls) : this(urls, new RemoteExecutorOptions(), NullLogger.Instance)
    {
    }

    public RemoteExecutor(string[] urls, ILogger logger) : this(urls, new RemoteExecutorOptions(), logger)
    {
    }

    public RemoteExecutor(string[] urls, Action<RemoteExecutorOptions> configure) : this(urls, NullLogger.Instance, configure)
    {
    }

    public RemoteExecutor(string[] urls, ILogger logger, Action<RemoteExecutorOptions> configure)
    {
        this.logger = logger;

        options = new RemoteExecutorOptions();
        globalQueue = new BlockingCollection<PendingTask>(options.GlobalQueueCapacity);

        configure(options);

        InitializeServers(urls);
    }

    public RemoteExecutor(string[] urls, RemoteExecutorOptions options, ILogger logger)
    {
        this.logger = logger;
        this.options = options;

        globalQueue = new BlockingCollection<PendingTask>(this.options.GlobalQueueCapacity);

        InitializeServers(urls);
    }

    private void InitializeServers(string[] urls)
    {
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

    public async Task<TResult> ExecuteAsync<TDelegate, TResult>(TDelegate @delegate, CancellationToken cancellationToken, params object[] args) where TDelegate : Delegate
    {
        object? execResult = await ExecuteAsync(@delegate, cancellationToken, args);

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

    public async Task<TResult> ExecuteAsync<TDelegate, TResult>(TDelegate @delegate, params object[] args) where TDelegate : Delegate
    {
        return await ExecuteAsync<TDelegate, TResult>(@delegate, CancellationToken.None, args);
    }

    public async Task<object?> ExecuteAsync<T>(T @delegate, CancellationToken cancellationToken, params object[] args) where T : Delegate
    {
        MethodInfo method = @delegate.Method;
        Type declaringType = method.DeclaringType!;
        Assembly assembly = declaringType.Assembly;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, distributorCts.Token);
        using CancellationTokenSource timeoutCts = new CancellationTokenSource(options.ExecutionTimeout);
        using CancellationTokenSource finalCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);

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

        try
        {
            RemoteExecutionResult result = await tcs.Task.WaitAsync(finalCts.Token);

            if (result.Exception != null)
            {
                throw new RemoteExecutionException(result.Exception);
            }

            return result.Result;
        }
        finally
        {
            _ = pendingResults.TryRemove(taskId, out _);
        }
    }

    public Task<object?> ExecuteAsync<T>(T @delegate, params object[] args) where T : Delegate
    {
        return ExecuteAsync(@delegate, CancellationToken.None, args);
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