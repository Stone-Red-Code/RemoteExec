using Microsoft.AspNetCore.SignalR;

using RemoteExec.Server.Hubs;

namespace RemoteExec.Server.Services;

public class MetricsBroadcastService(IHubContext<RemoteExecutionHub> hubContext, ILogger<MetricsBroadcastService> logger) : BackgroundService
{
    private readonly TimeSpan _broadcastInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Metrics broadcast service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_broadcastInterval, stoppingToken);

                await RemoteExecutionHub.BroadcastMetricsAsync(hubContext);
            }
            catch (OperationCanceledException)
            {
                // Expected when service is stopping
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error broadcasting metrics");
            }
        }

        logger.LogInformation("Metrics broadcast service stopped");
    }
}