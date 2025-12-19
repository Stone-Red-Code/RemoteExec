using Microsoft.AspNetCore.SignalR.Client;

using RemoteExec.Shared;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

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
        _ = _connection.On($"RequestAssembly", async (string assemblyName, Guid requestId) =>
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().FullName == assemblyName);

            if (assembly == null)
            {
                assembly = Assembly.Load(new AssemblyName(assemblyName));
            }

            byte[] dllBytes = await File.ReadAllBytesAsync(assembly.Location!);

            Channel<byte> channel = Channel.CreateUnbounded<byte>();

            foreach (byte b in dllBytes)
            {
                await channel.Writer.WriteAsync(b);
            }

            channel.Writer.Complete();

            await _connection.InvokeAsync("ProvideAssembly", requestId, channel.Reader);
        });

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
        Assembly assembly = declaringType.Assembly;

        if (!method.IsStatic)
        {
            throw new InvalidOperationException("Only static methods supported");
        }

        RemoteExecutionRequest request = new RemoteExecutionRequest
        {
            AssemblyName = assembly.GetName().FullName,
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