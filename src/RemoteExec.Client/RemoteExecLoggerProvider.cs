using Microsoft.Extensions.Logging;

namespace RemoteExec.Client;

/// <summary>
/// Logger provider that forwards log messages to an existing logger instance.
/// </summary>
internal class RemoteExecLoggerProvider : ILoggerProvider
{
    private bool disposedValue;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteExecLoggerProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger instance to forward messages to.</param>
    public RemoteExecLoggerProvider(ILogger logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        return logger;
    }

    /// <summary>
    /// Disposes the logger provider.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            // No managed or unmanaged resources to dispose
            disposedValue = true;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}