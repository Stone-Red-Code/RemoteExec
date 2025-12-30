namespace RemoteExec.Client.Exceptions;

/// <summary>
/// Exception thrown when a task exceeds the maximum number of retry attempts.
/// </summary>
public class MaxRetriesException(string message) : Exception(message)
{
}
