using Microsoft.AspNetCore.SignalR.Client;

using RemoteExec.Shared.Models;

namespace RemoteExec.Client;

/// <summary>
/// Provides data for the <see cref="RemoteExecutor.MetricsUpdated"/> event.
/// </summary>
public class ServerMetricsUpdatedEventArgs(HubConnection connection, ServerMetrics metrics) : EventArgs
{
    /// <summary>
    /// Gets the hub connection associated with the metrics update.
    /// </summary>
    public HubConnection Connection { get; } = connection;

    /// <summary>
    /// Gets the updated server metrics.
    /// </summary>
    public ServerMetrics Metrics { get; } = metrics;
}
