namespace RemoteExec.Server.Configuration;

public class MetricsConfiguration
{
    /// <summary>
    /// Minimum CPU usage difference (in percentage points) to trigger a metrics broadcast. Default is 1.0.
    /// </summary>
    public double CpuDifferenceThreshold { get; set; } = 1.0;

    /// <summary>
    /// Minimum memory usage difference (in bytes) to trigger a metrics broadcast. Default is 10 MB.
    /// </summary>
    public long MemoryDifferenceThreshold { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Broadcast interval in milliseconds. Default is 500 ms.
    /// </summary>
    public int BroadcastIntervalMs { get; set; } = 500;
}