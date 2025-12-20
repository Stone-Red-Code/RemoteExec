using CuteUtils.FluentMath.TypeExtensions;

using RemoteExec.Client;

// Single host example
RemoteExecutor singleHostExecutor = new RemoteExecutor("https://localhost:5001/remote");

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

bool success = singleHostExecutor.TryExecute(Multiply, out int result, 2, 4);
Console.WriteLine($"Success: {success}, Result: {result}");

result = singleHostExecutor.Execute<Func<int, int, Task<int>>, int>(Multiply, 3, 5);
Console.WriteLine($"Result: {result}");

await singleHostExecutor.StopAsync();

static async Task<int> Multiply(int x, int y)
{
    await Task.Delay(5000);
    return x.Multiply(y);
}