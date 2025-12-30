namespace RemoteExec.Shared.Models.Docker;

public class ContainerExecutionRequest
{
    public required string AssemblyBytes { get; set; }
    public required string TypeName { get; set; }
    public required string MethodName { get; set; }
    public required string[] ArgumentTypes { get; set; }
    public required object[] Arguments { get; set; }
}
