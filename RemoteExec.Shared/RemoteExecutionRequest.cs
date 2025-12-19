namespace RemoteExec.Shared;

public sealed class RemoteExecutionRequest
{
    public required byte[] AssemblyBytes { get; set; }
    public required string TypeName { get; set; }
    public required string MethodName { get; set; }
    public required string[] ArgumentTypes { get; set; }
    public required object[] Arguments { get; set; }
}
