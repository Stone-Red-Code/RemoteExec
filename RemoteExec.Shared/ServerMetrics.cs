namespace RemoteExec.Shared;

public sealed class ServerMetrics
{
    public int ActiveConnections { get; set; }
    public int ActiveTasks { get; set; }
    public int MaxConcurrentTasks { get; set; }
    public long TotalMemoryUsage { get; set; }
    public double CpuUsage { get; set; }
    public DateTime Timestamp { get; set; }
    public string ServerId { get; set; } = string.Empty;
}