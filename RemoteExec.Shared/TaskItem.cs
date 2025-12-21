namespace RemoteExec.Shared;

public class TaskItem
{
    public Guid TaskId { get; set; }
    public required RemoteExecutionRequest Request { get; set; }
}