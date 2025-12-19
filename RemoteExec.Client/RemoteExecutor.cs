using Microsoft.AspNetCore.SignalR.Client;

using RemoteExec.Shared;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace RemoteExec.Client;

public class RemoteExecutor(string url)
{
    private readonly HubConnection _connection =
        new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect()
            .Build();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _connection.StartAsync(cancellationToken);
    }

    public bool TryExecute<TDelegate, TResult>(TDelegate del, out TResult? result, params object[] args) where TDelegate : Delegate
    {
        try
        {
            result = Execute<TDelegate, TResult>(del, args);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public bool TryExecute<TDelegate, TResult>(TDelegate del, out TResult? result, [NotNullWhen(false)] out Exception? exception, params object[] args) where TDelegate : Delegate
    {
        try
        {
            result = Execute<TDelegate, TResult>(del, args);
            exception = null;
            return true;
        }
        catch (Exception ex)
        {
            result = default;
            exception = ex;
            return false;
        }
    }

    public TResult Execute<TDelegate, TResult>(TDelegate del, params object[] args) where TDelegate : Delegate
    {
        object? execResult = Execute(del, args);

        if (execResult is TResult typedResult)
        {
            return typedResult;
        }
        else if (execResult is JsonElement jsonElement)
        {
            return jsonElement.Deserialize<TResult>()!;
        }
        else
        {
            throw new InvalidCastException("The result cannot be cast to the specified type.");
        }
    }

    public object? Execute<T>(T del, params object[] args) where T : Delegate
    {
        MethodInfo method = del.Method;
        Type declaringType = method.DeclaringType!;
        Assembly asm = declaringType.Assembly;

        if (!method.IsStatic)
        {
            throw new InvalidOperationException("Only static methods supported");
        }

        if (asm.IsDynamic)
        {
            throw new InvalidOperationException("Dynamic assemblies are not supported");
        }

        if (string.IsNullOrEmpty(asm.Location))
        {
            throw new InvalidOperationException("Assembly location is not available");
        }

        byte[] dllBytes = File.ReadAllBytes(asm.Location);

        RemoteExecutionRequest request = new RemoteExecutionRequest
        {
            AssemblyBytes = dllBytes,
            TypeName = declaringType.FullName!,
            MethodName = method.Name,
            ArgumentTypes = [.. method.GetParameters().Select(p => p.ParameterType.AssemblyQualifiedName!)],
            Arguments = args
        };

        RemoteExecutionResult result = _connection
            .InvokeAsync<RemoteExecutionResult>("Execute", request)
            .GetAwaiter()
            .GetResult();

        if (result.Exception != null)
        {
            throw new RemoteExecutionException(result.Exception);
        }

        return result.Result;
    }
}