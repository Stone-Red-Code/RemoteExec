using RemoteExec.Client.LoadBalancingStrategies;

namespace RemoteExec.Client;

public class RemoteExecutorOptions
{
    public ILoadBalancingStrategy Strategy { get; set; } = new ResourceAwareStrategy();

    public string ApiKey { get; set; } = string.Empty;

    // How long to wait for a result before throwing a TimeoutException
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    // Backoff settings for server reconnections
    public TimeSpan ServerReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ServerReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    // Reliability: How many times a task can be requeued before failing
    public int MaxTaskRetries { get; set; } = 3;

    // Capacity: Stop accepting Execute calls if the global queue is too full
    public int GlobalQueueCapacity { get; set; } = 1000;
}
