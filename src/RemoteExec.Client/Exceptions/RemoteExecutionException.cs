namespace RemoteExec.Client.Exceptions;

/// <summary>
/// Exception thrown when a remote method execution fails on the server.
/// </summary>
public class RemoteExecutionException(string message) : Exception(message)
{
}