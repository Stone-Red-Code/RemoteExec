namespace RemoteExec.Shared.Models;

/// <summary>
/// Represents a request to execute a static method on a remote server.
/// </summary>
public sealed class RemoteExecutionRequest
{
    /// <summary>
    /// Gets or sets the full name of the assembly containing the method.
    /// </summary>
    public required string AssemblyName { get; set; }

    /// <summary>
    /// Gets or sets the full name of the type containing the method.
    /// </summary>
    public required string TypeName { get; set; }

    /// <summary>
    /// Gets or sets the name of the method to execute.
    /// </summary>
    public required string MethodName { get; set; }

    /// <summary>
    /// Gets or sets the assembly-qualified names of the method's parameter types.
    /// </summary>
    public required string[] ArgumentTypes { get; set; }

    /// <summary>
    /// Gets or sets the arguments to pass to the method.
    /// </summary>
    public required object[] Arguments { get; set; }
}
