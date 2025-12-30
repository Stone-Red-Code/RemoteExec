namespace RemoteExec.Client;

/// <summary>
/// Defines a strategy for selecting a server from a pool of available servers for task distribution.
/// </summary>
public interface ILoadBalancingStrategy
{
    /// <summary>
    /// Selects the most appropriate server from the available servers based on the strategy's logic.
    /// </summary>
    /// <param name="availableServers">The collection of available servers to choose from.</param>
    /// <returns>The selected server, or <c>null</c> if no suitable server is found.</returns>
    ServerConnection? SelectServer(IEnumerable<ServerConnection> availableServers);
}
