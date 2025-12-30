namespace RemoteExec.Client.LoadBalancingStrategies;

/// <summary>
/// A load balancing strategy that selects servers based on local backlog.
/// </summary>
public class LeastBacklogStrategy : ILoadBalancingStrategy
{
    /// <inheritdoc/>
    public ServerConnection? SelectServer(IEnumerable<ServerConnection> availableServers)
    {
        return availableServers.MinBy(s => s.TaskChannel.Reader.Count);
    }
}
