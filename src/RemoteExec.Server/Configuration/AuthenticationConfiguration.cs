namespace RemoteExec.Server.Configuration;

/// <summary>
/// Configuration for API key authentication.
/// </summary>
public class AuthenticationConfiguration
{
    /// <summary>
    /// Gets or sets the list of configured API keys.
    /// </summary>
    public List<ApiKeyConfiguration> ApiKeys { get; set; } = [];
}