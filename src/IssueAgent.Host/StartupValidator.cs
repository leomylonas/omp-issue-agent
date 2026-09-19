using IssueAgent.Configuration;
using Microsoft.Extensions.Options;

namespace IssueAgent.Host;

/// <summary>Performs global, non-destructive checks before readiness is advertised. Repository
/// connectivity is intentionally isolated: a single provider outage does not make the process
/// unusable.</summary>
public sealed partial class StartupValidator(
    IOptions<IssueAgentOptions> options,
    EffectiveIssueAgentConfiguration effectiveConfiguration,
    ProviderRegistry providers,
    IssueAgent.Git.IGitRepositoryManager git,
    DefaultBranchResolver defaultBranchResolver,
    ILogger<StartupValidator> logger)
{
    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        ValidateWorkspace(configuration.Workspace.RootPath);
        ValidateOmp(configuration.Omp.ExecutablePath);

        foreach (var providerOptions in effectiveConfiguration.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = providers.Get(providerOptions.Name);
            if (providerOptions.Source.IdentityOverride is null)
            {
                try
                {
                    _ = await provider.GetCurrentIdentityAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogProviderValidationWarning(logger, exception, provider.Name);
                }
            }
            foreach (var repositoryOptions in providerOptions.Repositories.Where(repository => repository.Enabled))
            {
                try
                {
                    var authentication = providers.GetGitAuthentication(repositoryOptions.Id);
                    await git.EnsureBareRepositoryAsync(repositoryOptions.Id, repositoryOptions.CloneUrl, authentication, cancellationToken).ConfigureAwait(false);
                    await git.FetchAsync(repositoryOptions.Id, authentication, cancellationToken).ConfigureAwait(false);
                    var repositoryRef = new IssueAgent.Providers.RepositoryRef(repositoryOptions.Id, repositoryOptions.OwnerOrNamespace, repositoryOptions.Name);
                    var targetBranch = await defaultBranchResolver
                        .ResolveAsync(provider.Name, repositoryRef, repositoryOptions.TargetBranch, cancellationToken)
                        .ConfigureAwait(false);
                    _ = await git.ResolveBranchCommitAsync(repositoryOptions.Id, targetBranch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogRepositoryValidationWarning(logger, provider.Name, repositoryOptions.Id, exception.GetType().Name);
                }
            }
        }
    }

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Repository startup validation failed for {Provider}/{Repository} with {ExceptionType}")]
    private static partial void LogRepositoryValidationWarning(ILogger logger, string provider, string repository, string exceptionType);

    private static void ValidateWorkspace(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException("IssueAgent workspace root path is required.");
        }

        Directory.CreateDirectory(rootPath);
        foreach (var child in new[] { "repos", "workflows", "omp" })
        {
            var path = Path.Combine(rootPath, child);
            Directory.CreateDirectory(path);
            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException($"IssueAgent workspace path '{path}' is unusable.");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Provider identity validation failed for {Provider}; polling will retry at runtime")]
    private static partial void LogProviderValidationWarning(ILogger logger, Exception exception, string provider);

    private static void ValidateOmp(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException($"Required OMP executable '{executablePath}' was not found.");
        }

        if (!OperatingSystem.IsWindows() && !File.GetUnixFileMode(executablePath).HasFlag(UnixFileMode.UserExecute))
        {
            throw new InvalidOperationException($"Required OMP executable '{executablePath}' is not executable.");
        }
    }
}
