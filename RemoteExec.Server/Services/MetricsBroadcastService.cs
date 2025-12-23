using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

using RemoteExec.Server.Configuration;
using RemoteExec.Server.Hubs;

namespace RemoteExec.Server.Services;

public class MetricsBroadcastService(IHubContext<RemoteExecutionHub> hubContext, ILogger<MetricsBroadcastService> logger, IOptions<MetricsConfiguration> metricsOptions) : BackgroundService
{
    private readonly TimeSpan broadcastInterval = TimeSpan.FromMilliseconds(metricsOptions.Value.BroadcastIntervalMs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Metrics broadcast service started with interval {Interval}ms", broadcastInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(broadcastInterval, stoppingToken);

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