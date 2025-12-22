using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

using RemoteExec.Shared;

using System.Collections.Concurrent;

namespace RemoteExec.Client;

public partial class RemoteExecutor
{
    private async Task DistributorLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PendingTask? pendingTask = null;

            try
            {
                // Select the best server based on metrics
                ServerConnection? bestServer = SelectBestServer();

                if (bestServer is null)
                {
                    await serverAvailableSignal.WaitAsync(cancellationToken);
                    continue;
                }

                // Check if there are tasks in the global queue
                pendingTask = globalQueue.Take(cancellationToken);

                // Track task assignment
                if (serverAssignedTasks.TryGetValue(bestServer, out ConcurrentDictionary<Guid, PendingTask>? taskDictionary))
                {
                    _ = taskDictionary.TryAdd(pendingTask.TaskId, pendingTask);
                }

                // Create task item with ID
                TaskItem taskItem = new TaskItem
                {
                    TaskId = pendingTask.TaskId,
                    Request = pendingTask.Request
                };

                // Push to server's channel - SignalR will stream it
                await bestServer.TaskChannel.Writer.WriteAsync(taskItem, cancellationToken);

            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in distributor loop");

                if (pendingTask != null)
                {
                    globalQueue.Add(pendingTask, cancellationToken);
                }

                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private ServerConnection? SelectBestServer()
    {
        if (servers.Count == 0)
        {
            return null;
        }

        // Only select servers that are connected
        IEnumerable<ServerConnection> connectedServers = servers.Where(s => s.Connection.State == HubConnectionState.Connected);

        if (!connectedServers.Any())
        {
            return null;
        }

        return loadBalancingStrategy switch
        {
            LoadBalancingStrategy.ResourceAware => connectedServers.MinBy(s =>
            {
                if (s.Metrics == null)
                {
                    return double.MaxValue;
                }

                double cpuScore = s.Metrics.CpuUsage;
                double activeTaskScore = s.Metrics.ActiveTasks * 10;
                double backlogScore = s.TaskChannel.Reader.Count * 50;

                return cpuScore + activeTaskScore + backlogScore;
            }),
            LoadBalancingStrategy.LeastBacklog => connectedServers.MinBy(s => s.TaskChannel.Reader.Count),

            _ => connectedServers.FirstOrDefault()
        };
    }
}