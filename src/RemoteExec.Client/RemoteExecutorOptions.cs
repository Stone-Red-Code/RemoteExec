using RemoteExec.Client.LoadBalancingStrategies;

namespace RemoteExec.Client;

/// <summary>
/// Configuration options for the remote executor client.
/// </summary>
public class RemoteExecutorOptions
{
    /// <summary>
    /// Gets or sets the load balancing strategy used to select servers for task execution.
    /// Default is <see cref="ResourceAwareStrategy"/>.
    /// </summary>
    public ILoadBalancingStrategy Strategy { get; set; } = new ResourceAwareStrategy();

    /// <summary>
    /// Gets or sets the API key used for authenticating with remote servers.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum time to wait for a task result before throwing a TimeoutException.
    /// Default is 5 minutes.
    /// </summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the initial delay before attempting to reconnect to a disconnected server.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan ServerReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum delay between server reconnection attempts.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan ServerReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the maximum number of times a failed task can be requeued before failing permanently.
    /// Default is 3.
    /// </summary>
    public int MaxTaskRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum number of tasks that can be queued globally before blocking new execute calls.
    /// Default is 1000.
    /// </summary>
    public int GlobalQueueCapacity { get; set; } = 1000;
}
