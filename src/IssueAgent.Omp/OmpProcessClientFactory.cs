namespace IssueAgent.Omp;

/// <summary>Starts the pinned OMP executable and returns a fully wired <see cref="IOmpClient"/>.</summary>
public static class OmpProcessClientFactory
{
    public static IOmpClient Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> allowedEnvironment,
        TimeSpan? shutdownGracePeriod = null)
    {
        var transport = NdjsonRpcTransport.Start(executablePath, arguments, workingDirectory, allowedEnvironment);
        return new OmpProcessClient(transport, shutdownGracePeriod ?? TimeSpan.FromSeconds(15));
    }
}
