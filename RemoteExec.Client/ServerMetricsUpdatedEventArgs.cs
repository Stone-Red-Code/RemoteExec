using Microsoft.AspNetCore.SignalR.Client;

using RemoteExec.Shared;

namespace RemoteExec.Client;

public class ServerMetricsUpdatedEventArgs(HubConnection connection, ServerMetrics metrics) : EventArgs
{
    public HubConnection Connection { get; } = connection;
    public ServerMetrics Metrics { get; } = metrics;
}
