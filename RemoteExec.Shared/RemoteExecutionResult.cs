namespace RemoteExec.Shared;

/// <summary>
/// Represents the result of a remote method execution.
/// </summary>
public sealed class RemoteExecutionResult
{
    /// <summary>
    /// Gets or sets the return value of the executed method, or null if the method returns void.
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// Gets or sets the exception message if the execution failed, or null if successful.
    /// </summary>
    public string? Exception { get; set; }
}
