using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

using RemoteExec.Shared.Models;

using System.Collections.Concurrent;
using System.Reflection;

namespace RemoteExec.Client;

public partial class RemoteExecutor
{
    private void RegisterEventHandlers(ServerConnection server)
    {
        // Unregister existing handlers to prevent duplicates
        UnregisterEventHandlers(server);

        _ = server.Connection.On<ServerMetrics>("MetricsUpdated", metrics => OnMetricsUpdated(server, metrics));
        _ = server.Connection.On<Guid, RemoteExecutionResult>("TaskResult", (taskId, result) => OnTaskResult(server, taskId, result));
        _ = server.Connection.On<string, Guid>("RequestAssembly", async (assemblyName, requestId) => await OnRequestAssemblyAsync(server, assemblyName, requestId));

        // Store the handler so it can be unregistered later
        server.ClosedEventHandler = async (error) => await OnConnectionClosedAsync(server, error);
        server.Connection.Closed += server.ClosedEventHandler;
    }

    private static void UnregisterEventHandlers(ServerConnection server)
    {
        server.Connection.Remove("MetricsUpdated");
        server.Connection.Remove("TaskResult");
        server.Connection.Remove("RequestAssembly");

        // Unregister the Closed event handler if it exists
        if (server.ClosedEventHandler != null)
        {
            server.Connection.Closed -= server.ClosedEventHandler;
            server.ClosedEventHandler = null;
        }
    }

    private void OnMetricsUpdated(ServerConnection server, ServerMetrics metrics)
    {
        server.Metrics = metrics;
        MetricsUpdated?.Invoke(this, new ServerMetricsUpdatedEventArgs(server.Connection, metrics));
    }

    private void OnTaskResult(ServerConnection server, Guid taskId, RemoteExecutionResult result)
    {
        // Remove completed task from server's assigned tasks
        if (serverAssignedTasks.TryGetValue(server, out ConcurrentDictionary<Guid, PendingTask>? taskDictionary))
        {
            _ = taskDictionary.TryRemove(taskId, out _);
        }

        if (pendingResults.TryRemove(taskId, out TaskCompletionSource<RemoteExecutionResult>? tcs))
        {
            tcs.SetResult(result);
        }
    }

    private async Task OnRequestAssemblyAsync(ServerConnection server, string assemblyName, Guid requestId)
    {
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().FullName == assemblyName) ?? Assembly.Load(new AssemblyName(assemblyName));

        if (string.IsNullOrEmpty(assembly.Location))
        {
            logger.LogWarning("Cannot provide assembly {AssemblyName}: Location is null or empty", assemblyName);
            return;
        }

        byte[] dllBytes = await File.ReadAllBytesAsync(assembly.Location);

        ByteArrayContent content = new(dllBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        _ = await server.HttpClient.PostAsync($"/provide-assembly?requestId={requestId}", content);
    }

    private async Task OnConnectionClosedAsync(ServerConnection server, Exception? error)
    {
        if (error != null)
        {
            logger.LogWarning(error, "Server connection closed unexpectedly: {ServerUrl}", server.HttpClient.BaseAddress);
        }
        else
        {
            logger.LogInformation("Server connection closed gracefully: {ServerUrl}", server.HttpClient.BaseAddress);
        }

        // Requeue all tasks assigned to this server
        await RequeuePendingTasksForServer(server);

        if (!distributorCts.IsCancellationRequested)
        {
            _ = Task.Run(() => AttemptReconnectionAsync(server, distributorCts.Token));
        }
    }
}