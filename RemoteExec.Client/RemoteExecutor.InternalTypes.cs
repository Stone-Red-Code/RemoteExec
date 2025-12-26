using RemoteExec.Shared.Models;

namespace RemoteExec.Client;

public partial class RemoteExecutor
{

    private sealed class PendingTask
    {
        public required Guid TaskId { get; init; }
        public required RemoteExecutionRequest Request { get; init; }
        public required DateTime EnqueuedAt { get; init; }
        public int RetryCount { get; set; } = 0;
    }
}