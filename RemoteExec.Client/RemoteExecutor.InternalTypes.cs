using Microsoft.AspNetCore.SignalR.Client;

using RemoteExec.Shared;

using System.Threading.Channels;

namespace RemoteExec.Client;

public partial class RemoteExecutor
{
    private sealed class ServerConnection(HubConnection connection, HttpClient httpClient)
    {
        public HubConnection Connection { get; } = connection;
        public HttpClient HttpClient { get; } = httpClient;
        public Channel<TaskItem> TaskChannel { get; } = Channel.CreateUnbounded<TaskItem>();
        public ServerMetrics? Metrics { get; set; }
        public Func<Exception?, Task>? ClosedEventHandler { get; set; }
    }

    private sealed class PendingTask
    {
        public required Guid TaskId { get; init; }
        public required RemoteExecutionRequest Request { get; init; }
        public required DateTime EnqueuedAt { get; init; }
    }
}