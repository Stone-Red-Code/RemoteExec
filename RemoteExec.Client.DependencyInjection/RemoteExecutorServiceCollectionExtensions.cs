// In RemoteExec.Client.DependencyInjection (Extension Project)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteExec.Client.DependencyInjection;

public static class RemoteExecutorServiceCollectionExtensions
{
    public static IServiceCollection AddRemoteExecutor(this IServiceCollection services, string[] urls, Action<RemoteExecutorOptions>? configure = null)
    {
        RemoteExecutorOptions options = new RemoteExecutorOptions();
        configure?.Invoke(options);

        _ = services.AddSingleton(options);

        _ = services.AddSingleton(sp =>
        {
            RemoteExecutorOptions opt = sp.GetRequiredService<RemoteExecutorOptions>();
            ILogger<RemoteExecutor> logger = sp.GetRequiredService<ILogger<RemoteExecutor>>();

            return new RemoteExecutor(urls, opt, logger);
        });

        return services;
    }
}