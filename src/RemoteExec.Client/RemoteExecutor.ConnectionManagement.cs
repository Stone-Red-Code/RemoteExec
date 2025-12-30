using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

using RemoteExec.Client.Exceptions;
using RemoteExec.Shared.Models;

using System.Collections.Concurrent;

namespace RemoteExec.Client;

public partial class RemoteExecutor
{
    /// <summary>
    /// Starts the remote executor, connecting to all configured servers and beginning task distribution.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Dispose previous cancellation token source if exists
        if (distributorCts != null && !distributorCts.IsCancellationRequested)
        {
            await distributorCts.CancelAsync();
            distributorCts.Dispose();
        }

        distributorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        List<Task> startTasks = [];

        foreach (ServerConnection server in servers)
        {
            RegisterEventHandlers(server);
            startTasks.Add(StartServerConnectionAsync(server, distributorCts.Token));
        }

        await Task.WhenAll(startTasks);

        // Check if at least one server is connected
        if (!servers.Any(s => s.Connection.State == HubConnectionState.Connected))
        {
            logger.LogWarning("No servers are currently connected. Tasks will be queued until a server becomes available.");
        }

        distributorTask = Task.Run(() => DistributorLoop(distributorCts.Token), distributorCts.Token);
    }

    /// <summary>
    /// Stops the remote executor, disconnecting from all servers and stopping task distribution.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Stopping RemoteExecutor...");

        await distributorCts.CancelAsync();

        if (distributorTask != null)
        {
            try
            {
                await distributorTask;
                logger.LogDebug("Distributor task completed successfully");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogError(ex, "Distributor task was canceled");
            }
        }

        logger.LogDebug("Completing task channels for {ServerCount} servers", servers.Count);
        foreach (ServerConnection server in servers)
        {
            server.TaskChannel.Writer.Complete();
        }

        List<Task> stopTasks = [];

        foreach (ServerConnection server in servers)
        {
            stopTasks.Add(server.Connection.StopAsync(cancellationToken));
        }

        await Task.WhenAll(stopTasks);
        logger.LogInformation("RemoteExecutor stopped successfully");
    }

    private async Task StartServerConnectionAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        try
        {
            await InitializeServerAsync(server, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to connect to server: {ServerUrl}. Will retry automatically.", server.HttpClient.BaseAddress);

            // Start background reconnection attempts
            _ = Task.Run(async () => await AttemptReconnectionAsync(server, cancellationToken), cancellationToken);
        }
    }

    private async Task AttemptReconnectionAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        int retryDelay = (int)options.ServerReconnectInitialDelay.TotalMilliseconds;
        int maxRetryDelay = (int)options.ServerReconnectMaxDelay.TotalMilliseconds;

        while (!cancellationToken.IsCancellationRequested && server.Connection.State == HubConnectionState.Disconnected)
        {
            try
            {
                await Task.Delay(retryDelay, cancellationToken);
                logger.LogInformation("Attempting to reconnect to server: {ServerUrl}", server.HttpClient.BaseAddress);

                await InitializeServerAsync(server, cancellationToken);

                break; // Successfully connected
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconnection attempt failed for server: {ServerUrl}. Retrying in {Delay}ms", server.HttpClient.BaseAddress, retryDelay);

                // Exponential backoff
                retryDelay = Math.Min(retryDelay * 2, maxRetryDelay);
            }
        }
    }

    private async Task InitializeServerAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        await server.Connection.StartAsync(cancellationToken);
        logger.LogInformation("Connected to server: {ServerUrl}", server.HttpClient.BaseAddress);

        // Fetch initial metrics
        server.Metrics = await server.Connection.InvokeAsync<ServerMetrics>("GetMetrics", cancellationToken);
        MetricsUpdated?.Invoke(this, new ServerMetricsUpdatedEventArgs(server.Connection, server.Metrics));

        // Start task stream
        await server.Connection.SendAsync("StartTaskStream", server.TaskChannel.Reader, cancellationToken);

        serverAvailableSignal.Set();
    }

    private async Task RequeuePendingTasksForServer(ServerConnection server)
    {
        while (server.TaskChannel.Reader.TryRead(out _)) { /* Discard stale items */ }

        if (!serverAssignedTasks.TryGetValue(server, out ConcurrentDictionary<Guid, PendingTask>? taskDict))
        {
            return;
        }

        List<Guid> taskIds = taskDict.Keys.ToList();
        int count = 0;

        foreach (Guid id in taskIds)
        {
            if (taskDict.TryRemove(id, out PendingTask? task) && pendingResults.ContainsKey(id))
            {
                task.RetryCount++;

                if (task.RetryCount <= options.MaxTaskRetries)
                {
                    globalQueue.Add(task);
                    count++;
                }
                else if (pendingResults.TryRemove(id, out TaskCompletionSource<RemoteExecutionResult>? tcs))
                {
                    tcs.SetException(new MaxRetriesException("Max retry attempts reached."));
                }
            }
        }

        // If no servers are left, block the distributor
        if (!servers.Any(s => s.Connection.State == HubConnectionState.Connected))
        {
            serverAvailableSignal.Reset();
        }

        logger.LogInformation("Successfully requeued {Count} tasks from {Url}", count, server.HttpClient.BaseAddress);
    }
}