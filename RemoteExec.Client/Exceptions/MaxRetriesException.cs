namespace RemoteExec.Client.Exceptions;

public class MaxRetriesException(string message) : Exception(message)
{
}
