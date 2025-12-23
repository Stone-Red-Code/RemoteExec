namespace RemoteExec.Client.LoadBalancingStrategies;

internal class ResourceAwareStrategy : ILoadBalancingStrategy
{
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
