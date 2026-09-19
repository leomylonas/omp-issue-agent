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
        var sessionDirectory = FindSessionDirectory(arguments);
        var transport = NdjsonRpcTransport.Start(executablePath, arguments, workingDirectory, allowedEnvironment);
        return new OmpProcessClient(transport, shutdownGracePeriod ?? TimeSpan.FromSeconds(15), sessionDirectory);
    }

    private static string FindSessionDirectory(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--session-dir", StringComparison.Ordinal) &&
                index + 1 < arguments.Count)
            {
                return arguments[index + 1];
            }

            const string prefix = "--session-dir=";
            if (arguments[index].StartsWith(prefix, StringComparison.Ordinal))
            {
                return arguments[index][prefix.Length..];
            }
        }

        throw new ArgumentException("OMP arguments must specify --session-dir.", nameof(arguments));
    }
}
