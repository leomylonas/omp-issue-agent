using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

public enum WorkflowOutcomeStatus
{
    Progressed,
    Waiting,
    Failed,
}

/// <summary>Result of one discrete workflow operation. <see cref="Message"/> is the human-facing
/// explanation posted/notified for <see cref="WorkflowOutcomeStatus.Waiting"/> and
/// <see cref="WorkflowOutcomeStatus.Failed"/> outcomes.</summary>
public sealed record WorkflowOutcome(WorkflowOutcomeStatus Status, WorkflowState State, string? Message = null);

/// <summary>Locates the single canonical IssueAgent comment on an issue by its hidden locator
/// marker (specification §17). Durable state is accepted only from the configured provider
/// identity when one is supplied; otherwise it is accepted only from the authenticated provider
/// identity. An untrusted user can reproduce the marker but cannot control workflow state.</summary>
public static class CanonicalCommentLocator
{
    public static async Task<ProviderComment?> FindAsync(
        IGitProvider provider,
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken,
        string? configuredIdentity = null)
    {
        var authoritativeIdentity = await ResolveAuthoritativeIdentityAsync(provider, configuredIdentity, cancellationToken).ConfigureAwait(false);

        ProviderComment? canonicalComment = null;
        await foreach (var comment in provider.GetIssueCommentsAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            if (!CanonicalCommentMarkdown.IsCanonicalComment(comment.Body) ||
                !string.Equals(comment.AuthorLogin, authoritativeIdentity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (canonicalComment is not null)
            {
                throw new CanonicalCommentCorruptException("Multiple comments from the authoritative provider identity contain the canonical state locator marker.");
            }

            canonicalComment = comment;
        }

        return canonicalComment;
    }

    public static async ValueTask<string> ResolveAuthoritativeIdentityAsync(
        IGitProvider provider,
        string? configuredIdentity,
        CancellationToken cancellationToken)
    {
        var authoritativeIdentity = string.IsNullOrWhiteSpace(configuredIdentity)
            ? (await provider.GetCurrentIdentityAsync(cancellationToken).ConfigureAwait(false)).Login
            : configuredIdentity.Trim();
        if (string.IsNullOrWhiteSpace(authoritativeIdentity))
        {
            throw new CanonicalCommentCorruptException("Authoritative provider identity has no login; canonical workflow state cannot be trusted.");
        }

        return authoritativeIdentity;
    }
}
