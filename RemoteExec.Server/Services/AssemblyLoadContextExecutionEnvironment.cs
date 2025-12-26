using RemoteExec.Server.Utilities;
using RemoteExec.Shared;

using System.Reflection;
using System.Text.Json;

namespace RemoteExec.Server.Services;

public class AssemblyLoadContextExecutionEnvironment : ExecutionEnvironment
{
    public override string Name => "AssemblyLoadContext";

    private RemoteJobAssemblyLoadContext? assemblyLoadContext;

    public override async Task<RemoteExecutionResult> ExecuteTaskAsync(RemoteExecutionRequest request)
    {
        if (assemblyLoadContext is null)
        {
            throw new InvalidOperationException("The execution environment has not been prepared.");
        }

        // Check if assembly is already loaded in the context
        Assembly? assembly = assemblyLoadContext.Assemblies.FirstOrDefault(a => a.GetName().FullName == request.AssemblyName);

        // If not loaded, request and load it into the context
        assembly ??= assemblyLoadContext.LoadFromBytes(await RequestAssemblyAsync(request.AssemblyName));

        Type type = assembly.GetType(request.TypeName, throwOnError: true)!;

        Type[] argTypes = request.ArgumentTypes
            .Select(Type.GetType)
            .ToArray()!;

        MethodInfo? method = type.GetMethod(
            request.MethodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            argTypes,
            modifiers: null) ?? throw new MissingMethodException(request.TypeName, request.MethodName);

        // Pre-load all referenced assemblies to avoid triggering Resolving event during Invoke
        await AssemblyUtilities.PreLoadReferencedAssembliesAsync(assemblyLoadContext, assembly, RequestAssemblyAsync);

        ParameterInfo[] parameters = method.GetParameters();

        if (parameters.Length != request.Arguments.Length)
        {
            throw new ArgumentException($"Argument count mismatch: expected {parameters.Length}, received {request.Arguments.Length}");
        }

        object?[] invokeArgs = new object?[request.Arguments.Length];

        for (int i = 0; i < invokeArgs.Length; i++)
        {
            Type targetType = parameters[i].ParameterType;
            object arg = request.Arguments[i];

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

        if (result is Task taskResult)
        {
            await taskResult.ConfigureAwait(false);
            Type returnType = method.ReturnType;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                PropertyInfo resultProperty = returnType.GetProperty("Result")!;
                result = resultProperty.GetValue(taskResult);
            }
            else
            {
                // For non-generic Task, result is null
                result = null;
            }
        }

        return new RemoteExecutionResult
        {
            Result = result
        };
    }

    public override Task PrepareEnvironmentAsync(CancellationToken cancellationToken)
    {
        assemblyLoadContext = new RemoteJobAssemblyLoadContext($"RemoteJob_{Guid.NewGuid()}");

        return Task.CompletedTask;
    }

    public override Task CleanupEnvironmentAsync(CancellationToken cancellationToken)
    {
        assemblyLoadContext?.Unload();
        assemblyLoadContext = null;
        return Task.CompletedTask;
    }
}
