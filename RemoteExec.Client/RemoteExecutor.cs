using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RemoteExec.Client.Exceptions;
using RemoteExec.Shared;

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace RemoteExec.Client;

/// <summary>
/// Manages remote execution of static methods across one or more server connections with load balancing and fault tolerance.
/// </summary>
public partial class RemoteExecutor : IAsyncDisposable
{
    private readonly BlockingCollection<PendingTask> globalQueue;
    private readonly List<ServerConnection> servers = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteExecutionResult>> pendingResults = new();
    private readonly ConcurrentDictionary<ServerConnection, ConcurrentDictionary<Guid, PendingTask>> serverAssignedTasks = new();

    private readonly AsyncManualResetEvent serverAvailableSignal = new(false);
    private CancellationTokenSource distributorCts = new();
    private Task? distributorTask;

    private readonly RemoteExecutorOptions options;
    private readonly ILogger logger;

    private bool disposedValue;

    /// <summary>
    /// Occurs when server metrics are updated.
    /// </summary>
    public event EventHandler<ServerMetricsUpdatedEventArgs>? MetricsUpdated;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteExecutor"/> class with a single server URL.
    /// </summary>
    /// <param name="url">The URL of the remote server.</param>
    /// <param name="configure">An action to configure the executor options.</param>
    public RemoteExecutor(string url, Action<RemoteExecutorOptions> configure) : this([url], NullLogger.Instance, configure)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteExecutor"/> class with a single server URL and logger.
    /// </summary>
    /// <param name="url">The URL of the remote server.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="configure">An action to configure the executor options.</param>
    public RemoteExecutor(string url, ILogger logger, Action<RemoteExecutorOptions> configure) : this([url], logger, configure)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteExecutor"/> class with multiple server URLs.
    /// </summary>
    /// <param name="urls">The URLs of the remote servers.</param>
    /// <param name="configure">An action to configure the executor options.</param>
    public RemoteExecutor(string[] urls, Action<RemoteExecutorOptions> configure) : this(urls, NullLogger.Instance, configure)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteExecutor"/> class with multiple server URLs and logger.
    /// </summary>
    /// <param name="urls">The URLs of the remote servers.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="configure">An action to configure the executor options.</param>
    public RemoteExecutor(string[] urls, ILogger logger, Action<RemoteExecutorOptions> configure)
    {
        this.logger = logger;

        options = new RemoteExecutorOptions();
        globalQueue = new BlockingCollection<PendingTask>(options.GlobalQueueCapacity);

        configure(options);

        InitializeServers(urls);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteExecutor"/> class with preconfigured options.
    /// </summary>
    /// <param name="urls">The URLs of the remote servers.</param>
    /// <param name="options">The executor options.</param>
    /// <param name="logger">The logger instance.</param>
    public RemoteExecutor(string[] urls, RemoteExecutorOptions options, ILogger logger)
    {
        this.logger = logger;
        this.options = options;

        globalQueue = new BlockingCollection<PendingTask>(this.options.GlobalQueueCapacity);

        InitializeServers(urls);
    }

    private void InitializeServers(string[] urls)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("API Key is required. Configure it in RemoteExecutorOptions.");
        }

        foreach (string url in urls)
        {
            Uri baseUri = new(url);
            Uri signalRUri = new(baseUri, "/remote");

            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(signalRUri, httpOptions =>
                {
                    httpOptions.Headers["X-API-Key"] = options.ApiKey;
                })
                .ConfigureLogging(logging =>
                {
                    _ = logging.AddProvider(new RemoteExecLoggerProvider(logger));
                })
                .Build();

            HttpClient httpClient = new()
            {
                BaseAddress = baseUri
            };

            httpClient.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);

            ServerConnection serverConnection = new ServerConnection(connection, httpClient);
            servers.Add(serverConnection);
            serverAssignedTasks[serverConnection] = new ConcurrentDictionary<Guid, PendingTask>();
        }
    }

    /// <summary>
    /// Gets the current metrics for all connected servers.
    /// </summary>
    /// <returns>A dictionary mapping server IDs to their metrics.</returns>
    public Dictionary<string, ServerMetrics> GetCurrentServerMetrics()
    {
        return servers
            .Select(server => server.Metrics)
            .Where(metrics => metrics != null)
            .ToDictionary(metrics => metrics!.ServerId, metrics => metrics!);
    }

    /// <summary>
    /// Executes a delegate remotely and returns the strongly-typed result.
    /// </summary>
    /// <typeparam name="TDelegate">The delegate type.</typeparam>
    /// <typeparam name="TResult">The return type.</typeparam>
    /// <param name="delegate">The delegate to execute.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <param name="args">The arguments to pass to the method.</param>
    /// <returns>The result of the remote execution.</returns>
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

    /// <summary>
    /// Executes a delegate remotely and returns the strongly-typed result.
    /// </summary>
    /// <typeparam name="TDelegate">The delegate type.</typeparam>
    /// <typeparam name="TResult">The return type.</typeparam>
    /// <param name="delegate">The delegate to execute.</param>
    /// <param name="args">The arguments to pass to the method.</param>
    /// <returns>The result of the remote execution.</returns>
    public async Task<TResult> ExecuteAsync<TDelegate, TResult>(TDelegate @delegate, params object[] args) where TDelegate : Delegate
    {
        return await ExecuteAsync<TDelegate, TResult>(@delegate, CancellationToken.None, args);
    }

    /// <summary>
    /// Executes a delegate remotely and returns the result.
    /// </summary>
    /// <typeparam name="T">The delegate type.</typeparam>
    /// <param name="delegate">The delegate to execute. Must be a static method.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <param name="args">The arguments to pass to the method.</param>
    /// <returns>The result of the remote execution.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the delegate is not a static method or uses a dynamic assembly.</exception>
    /// <exception cref="RemoteExecutionException">Thrown when the remote execution fails.</exception>
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

    /// <summary>
    /// Executes a delegate remotely and returns the result.
    /// </summary>
    /// <typeparam name="T">The delegate type.</typeparam>
    /// <param name="delegate">The delegate to execute. Must be a static method.</param>
    /// <param name="args">The arguments to pass to the method.</param>
    /// <returns>The result of the remote execution.</returns>
    public Task<object?> ExecuteAsync<T>(T @delegate, params object[] args) where T : Delegate
    {
        return ExecuteAsync(@delegate, CancellationToken.None, args);
    }

    /// <summary>
    /// Asynchronously disposes the executor and its resources.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(disposing: true);
        GC.SuppressFinalize(this);
    }
}