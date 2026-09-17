namespace IssueAgent.Omp;

/// <summary>
/// Builds the explicit allow-listed environment passed to the OMP process. OMP never receives the
/// full ambient IssueAgent process environment, and never receives provider/Git/SSH/Kubernetes
/// credentials unless a caller explicitly configures them as an OMP execution secret.
/// </summary>
public static class OmpEnvironment
{
    private static readonly string[] AlwaysAllowedNames = ["PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "TZ"];
    private static readonly string[] AlwaysAllowedPrefixes = ["LC_"];
    private static readonly string[] ProxyNames = ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy"];

    /// <summary>Builds the environment for an OMP process from the ambient process environment,
    /// OMP/Auth Broker connection settings, and explicitly configured per-repo/global execution
    /// variables. <paramref name="executionVariables"/> values are already resolved from their
    /// configured secret source; configuring one here is the operator's explicit trust decision.</summary>
    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string?> ambientEnvironment,
        IReadOnlyDictionary<string, string> ompConnectionSettings,
        IReadOnlyDictionary<string, string> executionVariables)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in AlwaysAllowedNames.Concat(ProxyNames))
        {
            if (ambientEnvironment.TryGetValue(name, out var value) && value is not null)
            {
                result[name] = value;
            }
        }

        foreach (var (name, value) in ambientEnvironment)
        {
            if (value is not null && AlwaysAllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                result[name] = value;
            }
        }

        foreach (var (name, value) in ompConnectionSettings)
        {
            result[name] = value;
        }

        // Execution variables are the operator's explicit, per-repo/global trust decision and may
        // override any ambient value, including proxy settings.
        foreach (var (name, value) in executionVariables)
        {
            result[name] = value;
        }

        return result;
    }
}
