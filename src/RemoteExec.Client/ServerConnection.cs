using Microsoft.AspNetCore.SignalR.Client;

using RemoteExec.Shared.Models;

using System.Threading.Channels;

namespace RemoteExec.Client;

/// <summary>
/// Represents a connection to a remote execution server, including communication channels and metrics.
/// </summary>
public class ServerConnection(HubConnection connection, HttpClient httpClient)
{
    /// <summary>
    /// Gets the SignalR hub connection to the server.
    /// </summary>
    public HubConnection Connection { get; } = connection;

    /// <summary>
    /// Gets the HTTP client for REST API communication with the server.
    /// </summary>
    public HttpClient HttpClient { get; } = httpClient;

    /// <summary>
    /// Gets the channel used to queue tasks for this server.
    /// </summary>
    public Channel<TaskItem> TaskChannel { get; } = Channel.CreateUnbounded<TaskItem>();

    /// <summary>
    /// Gets or sets the current metrics for this server.
    /// </summary>
    public ServerMetrics? Metrics { get; set; }

    /// <summary>
    /// Gets or sets the handler for connection closed events.
    /// </summary>
    public Func<Exception?, Task>? ClosedEventHandler { get; set; }
}