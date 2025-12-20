using RemoteExec.Server.Hubs;
using RemoteExec.Server.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// Add metrics broadcast background service
builder.Services.AddHostedService<MetricsBroadcastService>();

WebApplication app = builder.Build();

app.MapHub<RemoteExecutionHub>("/remote");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
}

//app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health");

await app.RunAsync();
