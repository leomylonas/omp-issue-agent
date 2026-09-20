using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Context;

public sealed record AgentContextBuilderOptions
{
    /// <summary>Bot comments are ignored by default (specification §16); configurable globally with
    /// per-repository override by the caller supplying a different value per call.</summary>
    public bool IgnoreBotComments { get; init; } = true;

    /// <summary>Related issues are followed one hop by default (specification §14).</summary>
    public int RelatedIssueTraversalDepth { get; init; } = 1;

    public AttachmentLimits AttachmentLimits { get; init; } = new();

    /// <summary>The provider login authorized to publish canonical state. Without it, locator
    /// markers remain OMP-visible because their source cannot be authenticated.</summary>
    public string? CanonicalCommentAuthor { get; init; }

    /// <summary>Configured enabled repositories whose issues may be read as related context.</summary>
    public IReadOnlySet<string> AllowedRepositoryIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Builds the structured <see cref="AgentContext"/> bundle OMP receives for planning,
/// implementation, revision, and conflict resolution (specification §14). Related issues are
/// read-only context: this builder never mutates them.
/// </summary>
public sealed class AgentContextBuilder(IGitProvider provider, AttachmentPipeline attachmentPipeline, AgentContextBuilderOptions options)
{
    public async Task<AgentContext> BuildAsync(
        RepositoryRef repository,
        long issueNumber,
        WorkflowState workflowState,
        PlanContext? currentPlan,
        ProviderMergeRequest? mergeRequest,
        string attachmentsDestinationDirectory,
        CancellationToken cancellationToken)
    {
        var remainingBudget = new RemainingBudget(options.AttachmentLimits.MaxTotalSizeBytes);

        var primaryIssue = await BuildIssueContextAsync(repository, issueNumber, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
            .ConfigureAwait(false);

        var relatedIssues = await TraverseRelatedIssuesAsync(
            repository, issueNumber, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
            .ConfigureAwait(false);

        var mergeRequestContext = mergeRequest is null
            ? null
            : await BuildMergeRequestContextAsync(repository, mergeRequest, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                .ConfigureAwait(false);

        return new AgentContext(primaryIssue, relatedIssues, currentPlan, mergeRequestContext, workflowState);
    }

    private async Task<IssueContext> BuildIssueContextAsync(
        RepositoryRef repository,
        long issueNumber,
        string attachmentsDestinationDirectory,
        RemainingBudget remainingBudget,
        CancellationToken cancellationToken)
    {
        var issue = await provider.GetIssueAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false);

        var attachments = new List<AttachmentReference>();
        attachments.AddRange(await attachmentPipeline
            .ProcessAsync(issue.Description, issue.DescriptionSource, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
            .ConfigureAwait(false));

        var humanComments = new List<HumanComment>();
        await foreach (var comment in provider.GetIssueCommentsAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            if (comment.IsBot && options.IgnoreBotComments ||
                await IsAuthoritativeCanonicalCommentAsync(comment, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            humanComments.Add(new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body, UpdatedAt: comment.UpdatedAt));
            attachments.AddRange(await attachmentPipeline
                .ProcessAsync(comment.Body, comment.Source, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                .ConfigureAwait(false));
        }

        return new IssueContext(repository.Id, issue.Number, issue.Title, issue.Description, [.. issue.Labels], humanComments, attachments);
    }

    private async Task<IReadOnlyList<RelatedIssueContext>> TraverseRelatedIssuesAsync(
        RepositoryRef repository,
        long rootIssueNumber,
        string attachmentsDestinationDirectory,
        RemainingBudget remainingBudget,
        CancellationToken cancellationToken)
    {
        if (options.RelatedIssueTraversalDepth <= 0)
        {
            return [];
        }

        var visited = new HashSet<(string RepositoryId, long IssueNumber)> { (repository.Id, rootIssueNumber) };
        var frontier = new List<(RepositoryRef Repository, long IssueNumber)> { (repository, rootIssueNumber) };
        var results = new List<RelatedIssueContext>();

        for (var depth = 0; depth < options.RelatedIssueTraversalDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<(RepositoryRef Repository, long IssueNumber)>();

            foreach (var (currentRepository, currentIssueNumber) in frontier)
            {
                await foreach (var relationship in provider
                    .GetIssueRelationshipsAsync(currentRepository, currentIssueNumber, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!options.AllowedRepositoryIds.Contains(relationship.Repository.Id))
                    {
                        continue;
                    }

                    var key = (relationship.Repository.Id, relationship.IssueNumber);
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    var relatedIssueContext = await BuildIssueContextAsync(
                        relationship.Repository, relationship.IssueNumber, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                        .ConfigureAwait(false);

                    results.Add(new RelatedIssueContext(relationship.Relationship, relatedIssueContext));
                    nextFrontier.Add((relationship.Repository, relationship.IssueNumber));
                }
            }

            frontier = nextFrontier;
        }

        return results;
    }


    /// <summary>Builds review/comment/attachment context for an already-located merge request.</summary>
    public async Task<MergeRequestContext> BuildMergeRequestContextAsync(
        RepositoryRef repository,
        ProviderMergeRequest mergeRequest,
        string attachmentsDestinationDirectory,
        RemainingBudget remainingBudget,
        CancellationToken cancellationToken)
    {
        var attachments = new List<AttachmentReference>();
        attachments.AddRange(await attachmentPipeline
            .ProcessAsync(mergeRequest.Description, mergeRequest.DescriptionSource, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
            .ConfigureAwait(false));

        var comments = new List<HumanComment>();
        await foreach (var comment in provider.GetMergeRequestCommentsAsync(repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false))
        {
            if (comment.IsBot && options.IgnoreBotComments ||
                await IsAuthoritativeCanonicalCommentAsync(comment, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            comments.Add(new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body, UpdatedAt: comment.UpdatedAt, CommentId: comment.Id));
            attachments.AddRange(await attachmentPipeline
                .ProcessAsync(comment.Body, comment.Source, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                .ConfigureAwait(false));
        }

        var reviewThreads = new List<HumanComment>();
        await foreach (var thread in provider.GetReviewThreadsAsync(repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false))
        {
            foreach (var comment in thread.Comments)
            {
                if (comment.IsBot && options.IgnoreBotComments ||
                    await IsAuthoritativeCanonicalCommentAsync(comment, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                reviewThreads.Add(new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body, thread.Id, thread.IsResolved, comment.UpdatedAt, comment.Id));
                attachments.AddRange(await attachmentPipeline
                    .ProcessAsync(comment.Body, comment.Source, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return new MergeRequestContext(mergeRequest.Number, mergeRequest.Description, comments, reviewThreads, attachments);
    }

    private ValueTask<bool> IsAuthoritativeCanonicalCommentAsync(ProviderComment comment, CancellationToken cancellationToken)
    {
        if (!CanonicalCommentMarkdown.IsCanonicalComment(comment.Body) ||
            string.IsNullOrWhiteSpace(options.CanonicalCommentAuthor))
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(
            CanonicalCommentMarkdown.IsAuthoritativeCanonicalComment(comment, options.CanonicalCommentAuthor.Trim()));
    }
}
