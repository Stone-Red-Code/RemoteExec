namespace RemoteExec.Shared;

/// <summary>
/// Represents a task item in the execution stream, combining a task ID with its execution request.
/// </summary>
public class TaskItem
{
    /// <summary>
    /// Gets or sets the unique identifier for this task.
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// Gets or sets the execution request details.
    /// </summary>
    public required RemoteExecutionRequest Request { get; set; }
}