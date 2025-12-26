using RemoteExec.Shared.Models.Docker;
using RemoteExec.Shared.Utilities;

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace RemoteExec.Worker;

public static class Program
{
    private const string AssemblyCachePath = "/tmp";

    public static async Task<int> Main()
    {
        try
        {
            string? requestBase64 = Environment.GetEnvironmentVariable("EXECUTION_REQUEST");

            if (string.IsNullOrEmpty(requestBase64))
            {
                await Console.Error.WriteLineAsync("EXECUTION_REQUEST environment variable not set");
                return 1;
            }

            byte[] requestBytes = Convert.FromBase64String(requestBase64);
            string requestJson = Encoding.UTF8.GetString(requestBytes);

            ContainerExecutionRequest? request = JsonSerializer.Deserialize<ContainerExecutionRequest>(requestJson);

            if (request == null)
            {
                await Console.Error.WriteLineAsync("Failed to deserialize execution request");
                return 1;
            }

            byte[] assemblyBytes = Convert.FromBase64String(request.AssemblyBytes);
            Assembly assembly = Assembly.Load(assemblyBytes);

            await AssemblyUtilities.PreLoadReferencedAssembliesAsync(AssemblyLoadContext.Default, assembly, RequestAssemblyAsync);

            Type? type = assembly.GetType(request.TypeName) ?? throw new TypeLoadException($"Type {request.TypeName} not found in assembly");
            Type[] argTypes = request.ArgumentTypes
                .Select(Type.GetType)
                .ToArray()!;

            MethodInfo? method = type.GetMethod(
                request.MethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                argTypes,
                modifiers: null);

            if (method == null)
            {
                throw new MissingMethodException($"Method {request.MethodName} not found in type {request.TypeName}");
            }

            ParameterInfo[] parameters = method.GetParameters();
            object?[] invokeArgs = new object?[request.Arguments.Length];

            for (int i = 0; i < invokeArgs.Length; i++)
            {
                Type targetType = parameters[i].ParameterType;
                object arg = request.Arguments[i];

                if (arg is JsonElement je)
                {
                    invokeArgs[i] = JsonSerializer.Deserialize(je.GetRawText(), targetType);
                }
                else if (arg == null)
                {
                    invokeArgs[i] = null;
                }
                else if (!targetType.IsInstanceOfType(arg))
                {
                    invokeArgs[i] = Convert.ChangeType(arg, targetType);
                }
                else
                {
                    invokeArgs[i] = arg;
                }
            }

            object? result = method.Invoke(null, invokeArgs);

            // Handle async methods
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
                    result = null;
                }
            }

            ContainerExecutionResponse response = new()
            {
                Success = true,
                Result = result
            };

            string responseJson = JsonSerializer.Serialize(response);
            await Console.Out.WriteLineAsync(responseJson);

            return 0;
        }
        catch (Exception ex)
        {
            ContainerExecutionResponse errorResponse = new()
            {
                Success = false,
                Exception = ex.ToString()
            };

            string errorJson = JsonSerializer.Serialize(errorResponse);
            await Console.Out.WriteLineAsync(errorJson);

            return 1;
        }
    }

    private static async Task<byte[]> RequestAssemblyAsync(string name)
    {
        string simpleName = new AssemblyName(name).Name!;
        await Console.Out.WriteLineAsync($"#REQUEST_ASSEMBLY {simpleName}#");
        await Console.Out.FlushAsync();

        string assemblyPath = Path.Combine(AssemblyCachePath, $"{simpleName}.dll");
        string sentinelPath = assemblyPath + ".ready";

        int maxAttempts = 100;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (File.Exists(sentinelPath))
            {
                byte[] assemblyBytes = await File.ReadAllBytesAsync(assemblyPath);

                File.Delete(sentinelPath);

                await Console.Out.WriteLineAsync($"#LOADED_ASSEMBLY {simpleName}#");
                return assemblyBytes;
            }

            await Task.Delay(10);
        }

        throw new FileNotFoundException($"Assembly {simpleName} timed out.");
    }
}
