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
/// marker (specification §17). Human edits to this comment are never authoritative control input.</summary>
public static class CanonicalCommentLocator
{
    public static async Task<ProviderComment?> FindAsync(IGitProvider provider, RepositoryRef repository, long issueNumber, CancellationToken cancellationToken)
    {
        ProviderComment? canonicalComment = null;
        await foreach (var comment in provider.GetIssueCommentsAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            if (!CanonicalCommentMarkdown.IsCanonicalComment(comment.Body))
            {
                continue;
            }

            if (canonicalComment is not null)
            {
                throw new CanonicalCommentCorruptException("Multiple comments contain the canonical state locator marker.");
            }

            canonicalComment = comment;
        }

        return canonicalComment;
    }
}
