namespace RemoteExec.Server.Configuration;

/// <summary>
/// Represents an individual API key configuration.
/// </summary>
public class ApiKeyConfiguration
{
    /// <summary>
    /// Gets or sets the API key value.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Gets or sets an optional description of the API key.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets whether this API key is enabled.
    /// Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}