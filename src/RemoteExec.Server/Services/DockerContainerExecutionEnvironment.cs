using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Options;

using RemoteExec.Server.Configuration;
using RemoteExec.Shared.Models;
using RemoteExec.Shared.Models.Docker;

using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace RemoteExec.Server.Services;

/// <summary>
/// Executes tasks in ephemeral Docker containers for maximum isolation.
/// </summary>
public class DockerContainerExecutionEnvironment : ExecutionEnvironment
{
    public override string Name => "DockerContainer";

    private readonly DockerClient dockerClient;
    private readonly ILogger<DockerContainerExecutionEnvironment> logger;
    private readonly string workerImageName;
    private readonly TimeSpan containerTimeout;
    private readonly long memoryLimit;
    private readonly long cpuLimit;
    private readonly bool networkDisabled;
    private readonly bool readOnlyFilesystem;

    private readonly ConcurrentDictionary<string, byte[]> assemblyCache = [];
    private readonly ConcurrentDictionary<string, string> runningContainers = [];

    public DockerContainerExecutionEnvironment(ILogger<DockerContainerExecutionEnvironment> logger, IOptions<DockerExecutionConfiguration> dockerConfig)
    {
        this.logger = logger;

        DockerExecutionConfiguration config = dockerConfig.Value;

        workerImageName = config.WorkerImageName;
        containerTimeout = TimeSpan.FromSeconds(config.ContainerTimeoutSeconds);
        memoryLimit = config.ContainerMemoryLimitMb * 1024 * 1024;
        cpuLimit = config.ContainerCpuShares;
        networkDisabled = config.DisableNetwork;
        readOnlyFilesystem = config.ReadOnlyFilesystem;

        DockerClientConfiguration dockerClientConfig;

        if (string.IsNullOrEmpty(config.DockerHost))
        {
            dockerClientConfig = new DockerClientConfiguration();
        }
        else
        {
            dockerClientConfig = new DockerClientConfiguration(new Uri(config.DockerHost));
        }

        dockerClient = dockerClientConfig.CreateClient();
    }

    public override Task PrepareEnvironmentAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override async Task<RemoteExecutionResult> ExecuteTaskAsync(RemoteExecutionRequest request)
    {
        string containerId = string.Empty;

        try
        {
            if (!assemblyCache.TryGetValue(request.AssemblyName, out byte[]? assemblyBytes))
            {
                assemblyBytes = await RequestAssemblyAsync(request.AssemblyName);
                assemblyCache[request.AssemblyName] = assemblyBytes;
            }

            ContainerExecutionRequest containerRequest = new()
            {
                AssemblyBytes = Convert.ToBase64String(assemblyBytes),
                TypeName = request.TypeName,
                MethodName = request.MethodName,
                ArgumentTypes = request.ArgumentTypes,
                Arguments = request.Arguments
            };

            string requestJson = JsonSerializer.Serialize(containerRequest);

            containerId = await CreateAndStartContainerAsync(requestJson, CancellationToken.None);

            using CancellationTokenSource timeoutCts = new(containerTimeout);
            Task logMonitorTask = MonitorContainerLogsAsync(containerId, timeoutCts.Token);

            ContainerWaitResponse waitResponse = await dockerClient.Containers.WaitContainerAsync(containerId, timeoutCts.Token);

            await timeoutCts.CancelAsync();

            try
            {
                await logMonitorTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // Get container logs (stdout contains JSON result)
            string stdout = await GetContainerLogsAsync(containerId);

            if (waitResponse.StatusCode != 0)
            {
                logger.LogError("Container {ContainerId} exited with code {ExitCode}", containerId, waitResponse.StatusCode);
                return new RemoteExecutionResult
                {
                    Exception = $"Container exited with code {waitResponse.StatusCode}\nOutput: {stdout}"
                };
            }

            string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string? resultLine = lines.LastOrDefault(l =>
            {
                string trimmed = l.TrimStart();
                return trimmed.StartsWith('{') && !trimmed.Contains("#REQUEST_ASSEMBLY") && !trimmed.Contains("#PROVIDE_ASSEMBLY");
            });

            if (resultLine == null)
            {
                return new RemoteExecutionResult
                {
                    Exception = $"No valid JSON result found in output: {stdout}"
                };
            }

            ContainerExecutionResponse? response = JsonSerializer.Deserialize<ContainerExecutionResponse>(resultLine);

            return new RemoteExecutionResult
            {
                Result = response?.Result,
                Exception = response?.Exception
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing task in container {ContainerId}", containerId);
            return new RemoteExecutionResult
            {
                Exception = ex.ToString()
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(containerId))
            {
                await CleanupContainerAsync(containerId);
            }
        }
    }

    public override async Task CleanupEnvironmentAsync(CancellationToken cancellationToken)
    {
        foreach (string containerId in runningContainers.Keys)
        {
            await CleanupContainerAsync(containerId);
        }

        assemblyCache.Clear();

        logger.LogInformation("Docker container execution environment cleaned up");
    }

    private async Task MonitorContainerLogsAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            MultiplexedStream logStream = await dockerClient.Containers.GetContainerLogsAsync(containerId, false, new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = false,
                Follow = true
            }, cancellationToken);

            byte[] buffer = new byte[4096];
            StringBuilder lineBuffer = new();

            while (!cancellationToken.IsCancellationRequested)
            {
                MultiplexedStream.ReadResult result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

                if (result.Count == 0)
                {
                    break;
                }

                string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _ = lineBuffer.Append(text);

                string bufferContent = lineBuffer.ToString();
                int lastNewline = bufferContent.LastIndexOf('\n');

                if (lastNewline == -1)
                {
                    continue;
                }

                string completeLines = bufferContent[..lastNewline];
                string remaining = bufferContent[(lastNewline + 1)..];

                _ = lineBuffer.Clear();
                _ = lineBuffer.Append(remaining);

                string[] lines = completeLines.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("#REQUEST_ASSEMBLY ") && trimmedLine.EndsWith('#'))
                    {
                        string assemblyName = trimmedLine.Substring("#REQUEST_ASSEMBLY ".Length, trimmedLine.Length - "#REQUEST_ASSEMBLY ".Length - 1);
                        _ = Task.Run(() => HandleAssemblyRequestAsync(containerId, assemblyName, cancellationToken), cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when container completes or timeout occurs
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error monitoring container {ContainerId} logs", containerId);
        }
    }

    private async Task HandleAssemblyRequestAsync(string containerId, string assemblyName, CancellationToken cancellationToken)
    {
        try
        {
            if (!assemblyCache.TryGetValue(assemblyName, out byte[]? assemblyBytes))
            {
                assemblyBytes = await RequestAssemblyAsync(assemblyName);
                assemblyCache[assemblyName] = assemblyBytes;
            }

            using MemoryStream tarStream = new();
            using (TarWriter tarWriter = new(tarStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                string fileName = $"{assemblyName}.dll";

                UstarTarEntry dllEntry = new(TarEntryType.RegularFile, fileName)
                {
                    DataStream = new MemoryStream(assemblyBytes)
                };
                await tarWriter.WriteEntryAsync(dllEntry, cancellationToken);

                UstarTarEntry sentinelEntry = new(TarEntryType.RegularFile, fileName + ".ready")
                {
                    DataStream = new MemoryStream()
                };

                await tarWriter.WriteEntryAsync(sentinelEntry, cancellationToken);
            }

            tarStream.Position = 0;

            ContainerPathStatParameters pathParams = new()
            {
                Path = "/tmp",
                AllowOverwriteDirWithFile = false,
            };

            await dockerClient.Containers.ExtractArchiveToContainerAsync(containerId, pathParams, tarStream, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error transferring assembly {AssemblyName} to container {ContainerId}", assemblyName, containerId);
        }
    }

    private async Task<string> CreateAndStartContainerAsync(string requestJson, CancellationToken cancellationToken)
    {
        CreateContainerParameters parameters = new()
        {
            Image = workerImageName,
            Name = $"remoteexec-task-{Guid.NewGuid()}",
            HostConfig = new HostConfig
            {
                Memory = memoryLimit,
                CPUShares = cpuLimit,
                NetworkMode = networkDisabled ? "none" : "bridge",
                ReadonlyRootfs = readOnlyFilesystem,
                AutoRemove = false,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges"],
                Tmpfs = new Dictionary<string, string>
                {
                    ["/tmp/assemblies"] = "rw,noexec,nosuid,size=100m"
                }
            },
            Env =
            [
                $"EXECUTION_REQUEST={Convert.ToBase64String(Encoding.UTF8.GetBytes(requestJson))}"
            ],
            WorkingDir = "/app",
            AttachStdout = true,
            AttachStderr = true
        };

        CreateContainerResponse container = await dockerClient.Containers.CreateContainerAsync(parameters, cancellationToken);

        bool started = await dockerClient.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), cancellationToken);

        if (!started)
        {
            throw new InvalidOperationException($"Failed to start container {container.ID}");
        }

        runningContainers[container.ID] = string.Empty;

        return container.ID;
    }

    private async Task<string> GetContainerLogsAsync(string containerId)
    {
        MultiplexedStream logStream = await dockerClient.Containers.GetContainerLogsAsync(containerId, false, new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true
        });

        StringBuilder output = new();
        byte[] buffer = new byte[4096];

        MultiplexedStream.ReadResult result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);

        while (result.Count > 0)
        {
            logger.LogDebug("Read {ByteCount} bytes from container {ContainerId} logs", result.Count, containerId);

            _ = output.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
        }

        return output.ToString();
    }

    private async Task CleanupContainerAsync(string containerId)
    {
        try
        {
            _ = await dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
            await dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true, RemoveVolumes = true });

            _ = runningContainers.TryRemove(containerId, out _);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanup container {ContainerId}", containerId);
        }
    }
}