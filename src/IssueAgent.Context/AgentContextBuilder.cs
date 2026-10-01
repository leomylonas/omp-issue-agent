using System.Collections.Concurrent;
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

    /// <summary>Configured enabled repositories whose issues may be read as related context.
    /// Repository references retain the configured provider-native identifier for follow-up reads.</summary>
    public IReadOnlyList<RepositoryRef> AllowedRepositories { get; init; } = [];
}

/// <summary>
/// Builds the structured <see cref="AgentContext"/> bundle OMP receives for planning,
/// implementation, revision, and conflict resolution (specification §14). Related issues are
/// read-only context: this builder never mutates them.
/// </summary>
public sealed class AgentContextBuilder(IGitProvider provider, AttachmentPipeline attachmentPipeline, AgentContextBuilderOptions options)
{
    private readonly ConcurrentDictionary<string, RemainingBudget> attachmentBudgets = new(StringComparer.Ordinal);
    public async Task<AgentContext> BuildAsync(
        RepositoryRef repository,
        long issueNumber,
        WorkflowState workflowState,
        PlanContext? currentPlan,
        ProviderMergeRequest? mergeRequest,
        string attachmentsDestinationDirectory,
        CancellationToken cancellationToken)
    {
        var remainingBudget = attachmentBudgets.GetOrAdd(
            Path.GetFullPath(attachmentsDestinationDirectory),
            attachmentPipeline.CreateRemainingBudget);

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

    /// <summary>Captures the same allowed, cycle-safe, depth-bounded related-issue graph that is
    /// supplied to OMP. Workflow input gates use these stamps so changes outside that graph cannot
    /// spuriously pause a workflow, while changes inside it are never missed.</summary>
    public async Task<IReadOnlyList<string>> CaptureRelatedIssueSnapshotAsync(
        RepositoryRef repository,
        long rootIssueNumber,
        CancellationToken cancellationToken)
    {
        if (options.RelatedIssueTraversalDepth <= 0)
        {
            return [];
        }

        var visited = new HashSet<(string RepositoryId, long IssueNumber)> { (repository.Id, rootIssueNumber) };
        var frontier = new List<(RepositoryRef Repository, long IssueNumber, IReadOnlyList<string> RelationshipPath)>
        {
            (repository, rootIssueNumber, []),
        };
        var stamps = new List<string>();

        for (var depth = 0; depth < options.RelatedIssueTraversalDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<(RepositoryRef Repository, long IssueNumber, IReadOnlyList<string> RelationshipPath)>();
            foreach (var (currentRepository, currentIssueNumber, relationshipPath) in frontier)
            {
                await foreach (var relationship in provider.GetIssueRelationshipsAsync(currentRepository, currentIssueNumber, cancellationToken).ConfigureAwait(false))
                {
                    var allowedRepository = FindAllowedRepository(relationship.Repository);
                    if (allowedRepository is null || !visited.Add((allowedRepository.Id, relationship.IssueNumber)))
                    {
                        continue;
                    }

                    var related = await CaptureRelatedIssueInputSnapshotAsync(
                        allowedRepository, relationship.IssueNumber, cancellationToken).ConfigureAwait(false);
                    var path = relationshipPath.Append(relationship.Relationship).ToArray();
                    stamps.Add($"{string.Join("->", path)}:{related}");
                    nextFrontier.Add((allowedRepository, relationship.IssueNumber, path));
                }
            }

            frontier = nextFrontier;
        }

        return stamps.OrderBy(stamp => stamp, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Records every related-issue input that can reach OMP, not just issue metadata.
    /// Related comments and attachment links can change without changing the issue's updated time.</summary>
    private async Task<string> CaptureRelatedIssueInputSnapshotAsync(
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var issue = await provider.GetIssueAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var inputs = new List<string>
        {
            $"repository:{repository.Id}",
            $"issue:{issue.Number}:{issue.Title}:{issue.Description}:{issue.UpdatedAt:O}:{string.Join(',', issue.Labels.OrderBy(label => label, StringComparer.Ordinal))}",
            $"attachments:{string.Join(',', ResolveAttachmentUrls(issue.Description))}",
        };

        await foreach (var comment in provider.GetIssueCommentsAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            if (comment.IsBot && options.IgnoreBotComments ||
                await IsAuthoritativeCanonicalCommentAsync(comment, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            inputs.Add($"comment:{comment.Id}:{comment.AuthorLogin}:{comment.CreatedAt:O}:{comment.UpdatedAt:O}:{comment.Body}");
            inputs.Add($"attachments:{comment.Id}:{string.Join(',', ResolveAttachmentUrls(comment.Body))}");
        }

        return string.Join('\u001f', inputs);
    }

    private IEnumerable<string> ResolveAttachmentUrls(string body) =>
        MarkdownAttachmentScanner.ScanLinks(body)
            .Select(provider.ResolveAttachmentUrl)
            .Where(url => url is not null)
            .Select(url => url!.OriginalString)
            .OrderBy(url => url, StringComparer.Ordinal);


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
        var frontier = new List<(RepositoryRef Repository, long IssueNumber, IReadOnlyList<string> RelationshipPath)>
        {
            (repository, rootIssueNumber, []),
        };
        var results = new List<RelatedIssueContext>();

        for (var depth = 0; depth < options.RelatedIssueTraversalDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<(RepositoryRef Repository, long IssueNumber, IReadOnlyList<string> RelationshipPath)>();

            foreach (var (currentRepository, currentIssueNumber, relationshipPath) in frontier)
            {
                await foreach (var relationship in provider
                    .GetIssueRelationshipsAsync(currentRepository, currentIssueNumber, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var allowedRepository = FindAllowedRepository(relationship.Repository);
                    if (allowedRepository is null)
                    {
                        continue;
                    }

                    var key = (allowedRepository.Id, relationship.IssueNumber);
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    var relatedIssueContext = await BuildIssueContextAsync(
                        allowedRepository, relationship.IssueNumber, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                        .ConfigureAwait(false);
                    var relatedIssueRelationshipPath = relationshipPath.Append(relationship.Relationship).ToArray();

                    results.Add(new RelatedIssueContext(relatedIssueRelationshipPath, relatedIssueContext));
                    nextFrontier.Add((allowedRepository, relationship.IssueNumber, relatedIssueRelationshipPath));
                }
            }

            frontier = nextFrontier;
        }

        return results;
    }

    private RepositoryRef? FindAllowedRepository(RepositoryRef relatedRepository) =>
        options.AllowedRepositories.FirstOrDefault(configuredRepository =>
            string.Equals(configuredRepository.OwnerOrNamespace, relatedRepository.OwnerOrNamespace, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(configuredRepository.Name, relatedRepository.Name, StringComparison.OrdinalIgnoreCase));


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

        var comments = new List<ProviderComment>();
        await foreach (var comment in provider.GetMergeRequestCommentsAsync(repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false))
        {
            if (comment.IsBot && options.IgnoreBotComments ||
                await IsAuthoritativeCanonicalCommentAsync(comment, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            comments.Add(comment);
        }

        var discussionNoteIds = new HashSet<long>();
        var reviewThreads = new List<HumanComment>();
        var reviewThreadComments = new List<ProviderComment>();
        await foreach (var thread in provider.GetReviewThreadsAsync(repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false))
        {
            foreach (var comment in thread.Comments)
            {
                discussionNoteIds.Add(comment.Id);
                if (comment.IsBot && options.IgnoreBotComments ||
                    await IsAuthoritativeCanonicalCommentAsync(comment, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                reviewThreads.Add(new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body, thread.Id, thread.IsResolved, comment.UpdatedAt, comment.Id));
                reviewThreadComments.Add(comment);
            }
        }

        var flatComments = new List<HumanComment>(comments.Count);
        foreach (var comment in comments)
        {
            if (discussionNoteIds.Contains(comment.Id))
            {
                continue;
            }

            flatComments.Add(new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body, UpdatedAt: comment.UpdatedAt, CommentId: comment.Id));
            attachments.AddRange(await attachmentPipeline
                .ProcessAsync(comment.Body, comment.Source, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                .ConfigureAwait(false));
        }

        foreach (var comment in reviewThreadComments)
        {
            attachments.AddRange(await attachmentPipeline
                .ProcessAsync(comment.Body, comment.Source, attachmentsDestinationDirectory, remainingBudget, cancellationToken)
                .ConfigureAwait(false));
        }

        return new MergeRequestContext(mergeRequest.Number, mergeRequest.Description, flatComments, reviewThreads, attachments);
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
