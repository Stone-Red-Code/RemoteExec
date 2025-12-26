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
    /// Options: "AssemblyLoadContext", "DockerContainer"
    /// </summary>
    public string ExecutionEnvironment { get; set; } = "AssemblyLoadContext";
}

public class DockerExecutionConfiguration
{
    /// <summary>
    /// Docker host URL. Default is unix:///var/run/docker.sock (Linux) or npipe://./pipe/docker_engine (Windows).
    /// </summary>
    public string DockerHost { get; set; } = string.Empty;

    /// <summary>
    /// Docker worker image name. Default is remoteexec-worker:latest.
    /// </summary>
    public string WorkerImageName { get; set; } = "remoteexec-worker:latest";

    /// <summary>
    /// Container timeout in seconds. Default is 300 (5 minutes).
    /// </summary>
    public int ContainerTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Memory limit per container in MB. Default is 512 MB.
    /// </summary>
    public long ContainerMemoryLimitMb { get; set; } = 512;

    /// <summary>
    /// CPU shares per container. Default is 1024.
    /// </summary>
    public long ContainerCpuShares { get; set; } = 1024;

    /// <summary>
    /// Disable network access in containers. Default is true.
    /// </summary>
    public bool DisableNetwork { get; set; } = true;

    /// <summary>
    /// Use read-only filesystem in containers. Default is true.
    /// </summary>
    public bool ReadOnlyFilesystem { get; set; } = true;
}