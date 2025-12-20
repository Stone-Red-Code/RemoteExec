using Microsoft.AspNetCore.SignalR.Client;

using RemoteExec.Shared;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

namespace RemoteExec.Client;

public class RemoteExecutor
{
    private readonly List<HubConnection> connections = [];
    private readonly ConcurrentDictionary<HubConnection, ServerMetrics> serverMetrics = new();
    private int _currentConnectionIndex = 0;
    private readonly Lock @lock = new Lock();
    private readonly LoadBalancingStrategy loadBalancingStrategy;

    public event EventHandler<ServerMetricsUpdatedEventArgs>? MetricsUpdated;

    public RemoteExecutor(string url) : this([url], LoadBalancingStrategy.RoundRobin)
    {
    }

    public RemoteExecutor(string[] urls, LoadBalancingStrategy loadBalancingStrategy = LoadBalancingStrategy.RoundRobin)
    {
        this.loadBalancingStrategy = loadBalancingStrategy;

        foreach (string url in urls)
        {
            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(url)
                .WithAutomaticReconnect()
                .Build();

            connections.Add(connection);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        List<Task> startTasks = [];

        foreach (HubConnection connection in connections)
        {
            _ = connection.On<ServerMetrics>("MetricsUpdated", metrics =>
            {
                serverMetrics[connection] = metrics;
                MetricsUpdated?.Invoke(this, new ServerMetricsUpdatedEventArgs(connection, metrics));
            });

            _ = connection.On($"RequestAssembly", async (string assemblyName, Guid requestId) =>
            {
                Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().FullName == assemblyName) ?? Assembly.Load(new AssemblyName(assemblyName));
                byte[] dllBytes = await File.ReadAllBytesAsync(assembly.Location!);

                Channel<byte> channel = Channel.CreateUnbounded<byte>();

                foreach (byte b in dllBytes)
                {
                    await channel.Writer.WriteAsync(b);
                }

                channel.Writer.Complete();

                await connection.InvokeAsync("ProvideAssembly", requestId, channel.Reader);
            });

            startTasks.Add(connection.StartAsync(cancellationToken)
                .ContinueWith(async (task, state) =>
                {
                    HubConnection conn = (HubConnection)state!;
                    serverMetrics[conn] = await conn.InvokeAsync<ServerMetrics>("GetMetrics", cancellationToken);
                    MetricsUpdated?.Invoke(this, new ServerMetricsUpdatedEventArgs(conn, serverMetrics[conn]));
                }, connection, TaskScheduler.Default).Unwrap());
        }

        await Task.WhenAll(startTasks);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        List<Task> stopTasks = [];

        foreach (HubConnection connection in connections)
        {
            stopTasks.Add(connection.StopAsync(cancellationToken));
        }

        await Task.WhenAll(stopTasks);
    }

    public Dictionary<string, ServerMetrics> GetCurrentServerMetrics()
    {
        Dictionary<string, ServerMetrics> metrics = [];

        foreach (HubConnection connection in connections)
        {
            if (serverMetrics.TryGetValue(connection, out ServerMetrics? newServerMetrics))
            {
                metrics[newServerMetrics.ServerId] = newServerMetrics;
            }
        }

        return metrics;
    }

    public bool TryExecute<TDelegate, TResult>(TDelegate del, out TResult? result, params object[] args) where TDelegate : Delegate
    {
        try
        {
            result = Execute<TDelegate, TResult>(del, args);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public bool TryExecute<TDelegate, TResult>(TDelegate del, out TResult? result, [NotNullWhen(false)] out Exception? exception, params object[] args) where TDelegate : Delegate
    {
        try
        {
            result = Execute<TDelegate, TResult>(del, args);
            exception = null;
            return true;
        }
        catch (Exception ex)
        {
            result = default;
            exception = ex;
            return false;
        }
    }

    public TResult Execute<TDelegate, TResult>(TDelegate del, params object[] args) where TDelegate : Delegate
    {
        object? execResult = Execute(del, args);

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

    public object? Execute<T>(T del, params object[] args) where T : Delegate
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

        HubConnection connection = GetNextConnection();

        RemoteExecutionResult result = connection
            .InvokeAsync<RemoteExecutionResult>("Execute", request)
            .GetAwaiter()
            .GetResult();

        if (result.Exception != null)
        {
            throw new RemoteExecutionException(result.Exception);
        }

        return result.Result;
    }

    private HubConnection GetNextConnection()
    {
        if (connections.Count == 0)
        {
            throw new InvalidOperationException("No connections available");
        }

        return loadBalancingStrategy switch
        {
            LoadBalancingStrategy.RoundRobin => GetRoundRobinConnection(),
            LoadBalancingStrategy.Random => GetRandomConnection(),
            LoadBalancingStrategy.LeastConnections => GetLeastConnections(),
            LoadBalancingStrategy.LeastActiveTasks => GetLeastActiveTasksConnections(),
            LoadBalancingStrategy.ResourceAware => GetResourceAwareConnections(),
            _ => throw new NotSupportedException($"Load balancing strategy {loadBalancingStrategy} is not supported")
        };
    }

    private HubConnection GetRoundRobinConnection()
    {
        lock (@lock)
        {
            HubConnection connection = connections[_currentConnectionIndex];
            _currentConnectionIndex = (_currentConnectionIndex + 1) % connections.Count;
            return connection;
        }
    }

    private HubConnection GetRandomConnection()
    {
        int index = Random.Shared.Next(connections.Count);
        return connections[index];
    }

    private HubConnection GetLeastConnections()
    {
        // Purely looks at how many clients are connected to the Hub
        return connections.OrderBy(c => serverMetrics.TryGetValue(c, out ServerMetrics? m) ? m.ActiveConnections : 0).First();
    }

    private HubConnection GetLeastActiveTasksConnections()
    {
        return connections.OrderBy(c => serverMetrics.TryGetValue(c, out ServerMetrics? m) ? m.ActiveTasks : 0).First();
    }

    private HubConnection GetResourceAwareConnections()
    {
        return connections.OrderBy(c =>
        {
            if (!serverMetrics.TryGetValue(c, out ServerMetrics? m))
            {
                return 0;
            }

            // Simple heuristic: CPU percentage + (Memory in MB / 1024)
            return m.CpuUsage + (m.TotalMemoryUsage / 1024 / 1024 / 100);
        }).First();
    }
}
