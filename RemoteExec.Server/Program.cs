using RemoteExec.Server.Hubs;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = null);
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

app.MapHub<RemoteExecutionHub>("/remote");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
