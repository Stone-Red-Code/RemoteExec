namespace RemoteExec.Client.LoadBalancingStrategies;

public class LeastBacklogStrategy : ILoadBalancingStrategy
{
    public ServerConnection? SelectServer(IEnumerable<ServerConnection> availableServers)
    {
        return availableServers.MinBy(s => s.TaskChannel.Reader.Count);
    }
}
