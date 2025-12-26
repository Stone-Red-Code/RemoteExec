namespace RemoteExec.Server.Configuration;

public class ExecutionConfiguration
{
    /// <summary>
    /// Maximum number of concurrent tasks. Default is ProcessorCount * 2.
    /// </summary>
    public int? MaxConcurrentTasks { get; set; }

    /// <summary>
    /// Timeout for assembly loading requests in seconds. Default is 30 seconds.
    /// </summary>
    public int AssemblyLoadTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Type of execution environment to use. Default is "AssemblyLoadContext".
    /// </summary>
    public string ExecutionEnvironment { get; set; } = "AssemblyLoadContext";
}