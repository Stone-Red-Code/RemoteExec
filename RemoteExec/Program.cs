using CuteUtils.FluentMath.TypeExtensions;

using RemoteExec.Client;

RemoteExecutor remoteExecutor = new RemoteExecutor("https://localhost:7109/remote");
await remoteExecutor.StartAsync();

bool success = remoteExecutor.TryExecute(Multiply, out int result, 2, 4);

Console.WriteLine($"Success: {success}, Result: {result}");

result = remoteExecutor.Execute<Func<int, int, int>, int>(Multiply, 3, 5);

Console.WriteLine($"Result: {result}");

static int Multiply(int x, int y)
{
    return x.Multiply(y);
}