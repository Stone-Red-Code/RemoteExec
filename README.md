# RemoteExec

![RemoteExec](https://socialify.git.ci/Stone-Red-Code/RemoteExec/image?custom_description=A+distributed+remote+execution+platform+for+C%23.&description=1&font=Inter&forks=1&issues=1&logo=https%3A%2F%2Fgithub-production-user-asset-6210df.s3.amazonaws.com%2F56473591%2F530980330-afd14ca4-7d88-4b2e-98ea-297162077d2a.svg%3FX-Amz-Algorithm%3DAWS4-HMAC-SHA256%26X-Amz-Credential%3DAKIAVCODYLSA53PQK4ZA%252F20251230%252Fus-east-1%252Fs3%252Faws4_request%26X-Amz-Date%3D20251230T130108Z%26X-Amz-Expires%3D300%26X-Amz-Signature%3D66c0d49953ce914d7de22049aaf9d1f9fb61036f8b69df045087011c34cb47ab%26X-Amz-SignedHeaders%3Dhost&name=1&pattern=Signal&pulls=1&stargazers=1&theme=Auto)

## What is it?

RemoteExec is a distributed computing platform that allows you to execute C# methods remotely across multiple servers using SignalR.

It enables seamless execution of static C# methods across multiple remote servers, featuring automatic load balancing, failover, real-time metrics, API key authentication.  
RemoteExec aims to make it easy to scale compute workloads or distribute jobs—no cloud lock-in and no complex configuration.

**Possible use cases include:**

- Parallel scientific/data computing
- Scaling out microservices and custom workflows
- Running distributed jobs in CI/CD pipelines
- Remotely triggering trusted code in a secure and observable way

## Usage

### Client

1. **Add the RemoteExec client library to your C# project:**
   - NuGet: `dotnet add package RemoteExec.Client`

2. **Connect to your RemoteExec server(s) and execute code:**

   ```csharp
   using RemoteExec.Client;

   // Configure the executor with your server URL(s) and API Key
   var executor = new RemoteExecutor("http://localhost:8080", opts => {
       opts.ApiKey = "your-key";
   });

   await executor.StartAsync();

   // Execute any static method remotely!
   int result = await executor.ExecuteAsync(Add, 2, 3);

   Console.WriteLine(result); // 5

   static int Add(int a, int b) => a + b;
   ```

### Server

1. **Download & install the latest release:**
    - Docker: `docker pull stonered/remoteexec-server`

1. **Run the server**

- Using the included Docker Compose (recommended from the `src/RemoteExec.Server` folder):

```bash
docker compose -f docker-compose.yml up
```

See [src/RemoteExec.Server/docker-compose.yml](src/RemoteExec.Server/docker-compose.yml) for details.

- Direct Docker run (alternative):

```bash
docker run -p 8080:80 \
   -e ASPNETCORE_ENVIRONMENT=Production \
   -v $(pwd)/appsettings.docker.json:/appsettings.json:ro \
   --name remoteexec-server stonered/remoteexec-server:latest
```

Configuration and API keys are controlled via `appsettings.json` (see [src/RemoteExec.Server/appsettings.json](src/RemoteExec.Server/appsettings.json)). To run with Docker Compose, copy and edit the example below into `src/RemoteExec.Server/appsettings.docker.json` and add your API keys.

## Follow the development

[![Twitter](https://img.shields.io/badge/Twitter-black.svg?&logo=x&style=for-the-badge&logoColor=white)](https://x.com/StoneRedCode)

## Example

See [/src/RemoteExec/Program.cs](src/RemoteExec/Program.cs)

```csharp
using CuteUtils.FluentMath.TypeExtensions;

using RemoteExec.Client;

// Configure the RemoteExecutor to connect to a single server
RemoteExecutor singleHostExecutor = new RemoteExecutor("https://localhost:5001/remote", configure =>
{
    configure.ApiKey = "dev-api-key-1";
});

await singleHostExecutor.StartAsync();

// Execute 1000 multiplications in parallel on the remote server
await Parallel.ForAsync(0, 1000, async (i, cancellationToken) =>
{
    int r = await singleHostExecutor.ExecuteAsync(Multiply, i, i + 1, cancellationToken);
    Console.WriteLine($"Multiply {i} * {i + 1} = {r}");
});

await singleHostExecutor.StopAsync();

static async Task<int> Multiply(int x, int y)
{
    await Task.Delay(Random.Shared.Next(100, 500));
    return x.Multiply(y);
}
```
