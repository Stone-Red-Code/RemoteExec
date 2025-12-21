using Microsoft.Extensions.Logging;

namespace RemoteExec.Client;

internal class RemoteExecLoggerProvider : ILoggerProvider
{
    private bool disposedValue;
    private readonly ILogger logger;

    public RemoteExecLoggerProvider(ILogger logger)
    {
        this.logger = logger;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return logger;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            // No managed or unmanaged resources to dispose
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}