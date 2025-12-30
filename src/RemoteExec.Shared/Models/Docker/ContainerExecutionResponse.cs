namespace RemoteExec.Shared.Models.Docker;

public class ContainerExecutionResponse
{
    public bool Success { get; set; }
    public object? Result { get; set; }
    public string? Exception { get; set; }
}
