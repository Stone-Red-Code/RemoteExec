namespace RemoteExec.Client;

public interface ILoadBalancingStrategy
{
    ServerConnection? SelectServer(IEnumerable<ServerConnection> availableServers);
}
