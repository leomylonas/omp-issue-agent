using System.Collections.Concurrent;
using IssueAgent.Providers;

namespace IssueAgent.Host;

/// <summary>Resolves each repository's effective target branch (specification §10: falls back to
/// the provider's default branch when no repository-level override is configured), caching the
/// provider lookup per repository so repeated dispatches do not re-query on every issue.</summary>
public sealed class DefaultBranchResolver(ProviderRegistry providers)
{
    private readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);

    public async ValueTask<string> ResolveAsync(
        string providerName,
        RepositoryRef repository,
        string? configuredTargetBranch,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredTargetBranch))
        {
            return configuredTargetBranch;
        }

        if (cache.TryGetValue(repository.Id, out var cached))
        {
            return cached;
        }

        var provider = providers.Get(providerName);
        var defaultBranch = await provider.GetDefaultBranchAsync(repository, cancellationToken).ConfigureAwait(false);
        cache[repository.Id] = defaultBranch;
        return defaultBranch;
    }
}
