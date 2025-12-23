namespace RemoteExec.Client.LoadBalancingStrategies;

/// <summary>
/// A load balancing strategy that selects servers based on CPU usage, active tasks, and backlog.
/// </summary>
internal class ResourceAwareStrategy : ILoadBalancingStrategy
{
    /// <inheritdoc/>
    public ServerConnection? SelectServer(IEnumerable<ServerConnection> availableServers)
    {
        return availableServers.MinBy(s =>
        {
            if (s.Metrics == null)
            {
                return double.MaxValue;
            }

            double cpuScore = s.Metrics.CpuUsage;
            double activeTaskScore = s.Metrics.ActiveTasks * 10;
            double backlogScore = s.TaskChannel.Reader.Count * 50;

            return cpuScore + activeTaskScore + backlogScore;
        });
    }
}
