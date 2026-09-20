using IssueAgent.Configuration;
using IssueAgent.Git;
using IssueAgent.Omp;

namespace IssueAgent.Host;

/// <summary>Builds the explicit environment passed to OMP from process-start resolved settings.
/// Ambient provider and Git credentials are never copied.</summary>
public sealed class OmpRuntimeEnvironmentFactory(EffectiveIssueAgentConfiguration configuration)
{
    public IReadOnlyDictionary<string, string> Create(
        IReadOnlyDictionary<string, string?> ambientEnvironment,
        GitIdentity gitIdentity,
        EffectiveRepositoryConfiguration repository)
    {
        ArgumentNullException.ThrowIfNull(gitIdentity);
        ArgumentNullException.ThrowIfNull(repository);
        var connection = new Dictionary<string, string>(configuration.Omp.ConnectionSettings, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(configuration.Omp.AuthBrokerUrl))
        {
            connection["OMP_AUTH_BROKER_URL"] = configuration.Omp.AuthBrokerUrl;
        }

        var executionValues = new Dictionary<string, string>(repository.OmpExecutionVariables, StringComparer.Ordinal);
        foreach (var (name, value) in repository.OmpExecutionSecrets)
        {
            executionValues[name] = value;
        }

        // OMP performs commits inside the worktree. Apply identity after configured execution
        // variables so the repository's resolved GitIdentity always governs those commits.
        var environment = new Dictionary<string, string>(
            OmpEnvironment.Build(ambientEnvironment, connection, executionValues),
            StringComparer.Ordinal);
        environment["GIT_AUTHOR_NAME"] = gitIdentity.Name;
        environment["GIT_AUTHOR_EMAIL"] = gitIdentity.Email;
        environment["GIT_COMMITTER_NAME"] = gitIdentity.Name;
        environment["GIT_COMMITTER_EMAIL"] = gitIdentity.Email;
        return environment;
    }
}
