namespace RemoteExec.Server.Configuration;

public class ApiKeyConfiguration
{
    public required string Key { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
}