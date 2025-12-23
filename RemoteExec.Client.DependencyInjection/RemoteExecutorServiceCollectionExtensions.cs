// In RemoteExec.Client.DependencyInjection (Extension Project)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteExec.Client.DependencyInjection;

/// <summary>
/// Extension methods for registering RemoteExecutor with dependency injection.
/// </summary>
public static class RemoteExecutorServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton RemoteExecutor to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="urls">The URLs of the remote execution servers.</param>
    /// <param name="configure">An optional action to configure the executor options.</param>
    /// <returns>The service collection for chaining.</returns>
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