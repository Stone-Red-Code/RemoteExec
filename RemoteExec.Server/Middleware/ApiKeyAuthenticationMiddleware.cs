using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using RemoteExec.Server.Configuration;

using System.Collections.Concurrent;

namespace RemoteExec.Server.Middleware;

public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;
    private readonly ConcurrentDictionary<string, ApiKeyConfiguration> _apiKeys;

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        // Build lookup dictionary from configuration
        _apiKeys = new ConcurrentDictionary<string, ApiKeyConfiguration>();

        // Initial load
        LoadApiKeys(authOptions.CurrentValue);

        // Watch for configuration changes
        _ = authOptions.OnChange(LoadApiKeys);
    }

    private void LoadApiKeys(AuthenticationConfiguration config)
    {
        _apiKeys.Clear();

        if (config.ApiKeys == null || config.ApiKeys.Count == 0)
        {
            _logger.LogWarning("No API keys configured. All requests will be rejected.");
            return;
        }

        foreach (ApiKeyConfiguration apiKey in config.ApiKeys.Where(k => k.Enabled))
        {
            if (string.IsNullOrWhiteSpace(apiKey.Key))
            {
                _logger.LogWarning("Skipping empty API key configuration");
                continue;
            }

            if (_apiKeys.TryAdd(apiKey.Key, apiKey))
            {
                _logger.LogInformation(
                    "Registered API key: {Description}",
                    apiKey.Description ?? "No description");
            }
            else
            {
                _logger.LogWarning(
                    "Duplicate API key found and skipped: {Description}",
                    apiKey.Description ?? "No description");
            }
        }

        _logger.LogInformation("Loaded {Count} active API keys", _apiKeys.Count);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip authentication for health checks
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        // Check if any API keys are configured
        if (_apiKeys.IsEmpty)
        {
            _logger.LogError("No API keys configured. Rejecting request to {Path}", context.Request.Path);

            context.Response.StatusCode = 503;

            await context.Response.WriteAsync("Service is not properly configured");
            return;
        }

        // Check for API key in header
        if (!context.Request.Headers.TryGetValue("X-API-Key", out StringValues extractedApiKey))
        {
            _logger.LogWarning("API Key missing from request to {Path} from {RemoteIp}", context.Request.Path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = 401;

            await context.Response.WriteAsync("API Key is missing");
            return;
        }

        string providedKey = extractedApiKey.ToString();

        // Validate API key
        if (!_apiKeys.TryGetValue(providedKey, out ApiKeyConfiguration? apiKeyConfig))
        {
            _logger.LogWarning("Invalid API Key provided for request to {Path} from {RemoteIp}",
                context.Request.Path,
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid API Key");
            return;
        }

        // Store API key info in HttpContext for potential use in controllers
        context.Items["ApiKeyDescription"] = apiKeyConfig.Description;
        context.Items["ApiKey"] = providedKey;

        _logger.LogDebug("Authenticated request to {Path} using key: {Description}",
            context.Request.Path,
            apiKeyConfig.Description ?? "No description");

        await _next(context);
    }
}