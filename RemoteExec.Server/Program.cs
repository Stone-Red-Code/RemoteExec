using RemoteExec.Server.Configuration;
using RemoteExec.Server.Hubs;
using RemoteExec.Server.Middleware;
using RemoteExec.Server.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

builder.Services.Configure<AuthenticationConfiguration>(builder.Configuration.GetSection("Authentication"));
builder.Services.Configure<ExecutionConfiguration>(builder.Configuration.GetSection("Execution"));
builder.Services.Configure<MetricsConfiguration>(builder.Configuration.GetSection("Metrics"));
builder.Services.AddScoped<ExecutionEnvironment, AssemblyLoadContextExecutionEnvironment>();

builder.Services.AddHostedService<MetricsBroadcastService>();

WebApplication app = builder.Build();

app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

app.MapHub<RemoteExecutionHub>("/remote");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health");

await app.RunAsync();
