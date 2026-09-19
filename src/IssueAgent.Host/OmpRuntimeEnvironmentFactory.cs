using IssueAgent.Configuration;
using IssueAgent.Omp;

namespace IssueAgent.Host;

/// <summary>Builds the explicit environment passed to OMP from process-start resolved settings.
/// Ambient provider and Git credentials are never copied.</summary>
public sealed class OmpRuntimeEnvironmentFactory(EffectiveIssueAgentConfiguration configuration)
{
    public IReadOnlyDictionary<string, string> Create(IReadOnlyDictionary<string, string?> ambientEnvironment)
    {
        var connection = new Dictionary<string, string>(configuration.Omp.ConnectionSettings, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(configuration.Omp.AuthBrokerUrl))
        {
            connection["OMP_AUTH_BROKER_URL"] = configuration.Omp.AuthBrokerUrl;
        }
        return OmpEnvironment.Build(ambientEnvironment, connection, configuration.Omp.ExecutionSecrets);
    }
}
