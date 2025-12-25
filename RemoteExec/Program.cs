using CuteUtils.FluentMath.TypeExtensions;

using RemoteExec.Client;

// Single host example
RemoteExecutor singleHostExecutor = new RemoteExecutor("https://localhost:5001/remote", configure =>
{
    configure.ApiKey = "dev-api-key-1";
});

singleHostExecutor.MetricsUpdated += (sender, e) =>
{
    Console.WriteLine($"[METRICS UPDATE] Server: {e.Metrics.ServerId}");
    Console.WriteLine($"  Active Connections: {e.Metrics.ActiveConnections}");
    Console.WriteLine($"  Pending Requests: {e.Metrics.ActiveTasks}");
    Console.WriteLine($"  CPU Usage: {e.Metrics.CpuUsage}%");
    Console.WriteLine($"  Memory: {e.Metrics.TotalMemoryUsage / 1024 / 1024} MB");
    Console.WriteLine();
};

await singleHostExecutor.StartAsync();

await Parallel.ForAsync(0, 1000, async (i, cancellationToken) =>
{
    int r = await singleHostExecutor.ExecuteAsync(Multiply, i, i + 1, cancellationToken);
    Console.WriteLine($"Multiply {i} * {i + 1} = {r}");

    int s = await singleHostExecutor.ExecuteAsync(Add, i, i + 1, cancellationToken);
    Console.WriteLine($"Add {i} + {i + 1} = {s}");
});

await singleHostExecutor.StopAsync();

static async Task<int> Multiply(int x, int y)
{
    await Task.Delay(Random.Shared.Next(100, 500));
    return x.Multiply(y);
}

static int Add(int x, int y)
{
    return x + y;
}