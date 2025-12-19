using Microsoft.AspNetCore.SignalR;

using RemoteExec.Shared;

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace RemoteExec.Server.Hubs;

public class RemoteExecutionHub : Hub
{
    private readonly Dictionary<string, AssemblyLoadContext> connections = [];

    public override Task OnConnectedAsync()
    {
        connections.Add(Context.ConnectionId, new RemoteJobAssemblyLoadContext($"RemoteJob_{Guid.NewGuid()}", true));

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (connections.TryGetValue(Context.ConnectionId, out RemoteJobAssemblyLoadContext assemblyLoadContext))
        {
            assemblyLoadContext.Unload();

            _ = connections.Remove(Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    public async Task<RemoteExecutionResult> Execute(RemoteExecutionRequest req)
    {
        try
        {
            AssemblyLoadContext alc = new AssemblyLoadContext(
                name: $"RemoteJob_{Guid.NewGuid()}",
                isCollectible: true);

            using MemoryStream ms = new MemoryStream(req.AssemblyBytes);
            Assembly asm = alc.LoadFromStream(ms);

            Type type = asm.GetType(req.TypeName, throwOnError: true)!;

            Type?[] argTypes = req.ArgumentTypes
                .Select(Type.GetType)
                .ToArray()!;

            MethodInfo? method = type.GetMethod(
                req.MethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                argTypes,
                modifiers: null);

            if (method == null)
            {
                throw new MissingMethodException(req.TypeName, req.MethodName);
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != req.Arguments.Length)
            {
                throw new ArgumentException("Argument count mismatch");
            }

            object?[] invokeArgs = new object?[req.Arguments.Length];

            for (int i = 0; i < invokeArgs.Length; i++)
            {
                Type targetType = parameters[i].ParameterType;
                object arg = req.Arguments[i];

                if (arg is JsonElement je)
                {
                    // Deserialize the JSON element into the expected CLR type
                    invokeArgs[i] = JsonSerializer.Deserialize(je.GetRawText(), targetType);
                }
                else if (arg == null)
                {
                    invokeArgs[i] = null;
                }
                else if (!targetType.IsInstanceOfType(arg))
                {
                    // Fallback for simple primitive conversions
                    invokeArgs[i] = Convert.ChangeType(arg, targetType);
                }
                else
                {
                    invokeArgs[i] = arg;
                }
            }

            object? result = method.Invoke(null, invokeArgs);

            alc.Unload();

            return new RemoteExecutionResult
            {
                Result = result
            };
        }
        catch (Exception ex)
        {
            return new RemoteExecutionResult
            {
                Exception = ex.ToString()
            };
        }
    }
}