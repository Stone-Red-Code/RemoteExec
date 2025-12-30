namespace RemoteExec.Shared.Models;

/// <summary>
/// Represents performance and status metrics for a remote execution server.
/// </summary>
public sealed class ServerMetrics
{
    /// <summary>
    /// Gets or sets the number of active client connections to the server.
    /// </summary>
    public int ActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets the number of tasks currently being executed on the server.
    /// </summary>
    public int ActiveTasks { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of tasks that can execute concurrently on the server.
    /// </summary>
    public int MaxConcurrentTasks { get; set; }

    /// <summary>
    /// Gets or sets the total memory usage of the server process in bytes.
    /// </summary>
    public long TotalMemoryUsage { get; set; }

    /// <summary>
    /// Gets or sets the CPU usage percentage (0-100) of the server process.
    /// </summary>
    public double CpuUsage { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when these metrics were captured.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier for the server.
    /// </summary>
    public string ServerId { get; set; } = string.Empty;
}