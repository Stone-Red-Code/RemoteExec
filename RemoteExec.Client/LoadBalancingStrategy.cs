namespace RemoteExec.Client;

public enum LoadBalancingStrategy
{
    RoundRobin,
    Random,
    LeastConnections,
    LeastActiveTasks,
    ResourceAware
}