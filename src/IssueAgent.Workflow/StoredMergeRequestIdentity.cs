using IssueAgent.Context;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

/// <summary>Validates the repository-scoped request identity stored in canonical workflow state
/// before it is used to retrieve a pull or merge request.</summary>
public static class StoredMergeRequestIdentity
{
    public static long Parse(RepositoryRef repository, string identity)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new CanonicalStateException("Canonical state pullOrMergeRequest is not a valid repository request identity.");
        }

        var prefix = $"{repository.Id}#";
        if (!identity.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(identity[prefix.Length..], out var number) ||
            number <= 0)
        {
            throw new CanonicalStateException("Canonical state pullOrMergeRequest is not a valid repository request identity.");
        }

        return number;
    }

    public static async Task<ProviderMergeRequest?> FindAsync(
        IGitProvider provider,
        RepositoryRef repository,
        string? identity,
        CancellationToken cancellationToken) =>
        identity is null
            ? null
            : await provider.GetMergeRequestAsync(repository, Parse(repository, identity), cancellationToken).ConfigureAwait(false);
}
