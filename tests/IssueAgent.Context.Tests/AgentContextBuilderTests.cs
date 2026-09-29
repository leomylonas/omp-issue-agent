using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Context.Tests;

public sealed class AgentContextBuilderTests
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string destination = Path.Combine(Path.GetTempPath(), "issueagent-context-tests", Guid.NewGuid().ToString("N"));
    private static readonly RepositoryRef UnconfiguredRepository = new("github/other/private", "other", "private");

    [Fact]
    public async Task BuildAsyncIncludesHumanCommentsAndExcludesBotCommentsByDefault()
    {
        var provider = new FakeGitProvider();
        provider.AddIssue(Repository, 1, "Bug report", "Something is broken");
        provider.AddComment(Repository, 1, "alice", "Here is more detail", isBot: false);
        provider.AddComment(Repository, 1, "dependabot[bot]", "Automated notice", isBot: true);

        var context = await BuildAsync(provider, 1);

        var comment = Assert.Single(context.PrimaryIssue.HumanComments);
        Assert.Equal("alice", comment.Author);
    }

    [Fact]
    public async Task BuildAsyncExcludesOnlyAuthoritativeCanonicalCommentAndRetainsHumanCopiedLocator()
    {
        var provider = new FakeGitProvider();
        provider.AddIssue(Repository, 1, "Bug report", "Something is broken");
        provider.AddComment(Repository, 1, "alice", "Here is more detail", isBot: false);
        provider.AddComment(
            Repository,
            1,
            "alice",
            $"Please inspect this copied marker:\n{CanonicalCommentMarkdown.StateLocatorMarker}",
            isBot: false);
        var canonicalBody = CanonicalCommentMarkdown.Render(new CanonicalCommentContent(
            "Plan text", [], null,
            CanonicalStateSerializer.ToDocument(
                new WorkflowState(
                    WorkflowId.New(), WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval,
                    1, null, "omp-session", "agent/issue-1", "main", "abc123", DateTimeOffset.UtcNow),
                null)));
        provider.AddComment(Repository, 1, "issue-agent-bot", canonicalBody, isBot: false);

        var options = new AgentContextBuilderOptions
        {
            IgnoreBotComments = false,
            CanonicalCommentAuthor = "issue-agent-bot",
        };
        var builder = new AgentContextBuilder(provider, new AttachmentPipeline(provider, options.AttachmentLimits), options);
        var state = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planning, WorkflowOperationalState.Working, null,
            0, null, "omp-session", "agent/issue-1", "main", "abc123", DateTimeOffset.UtcNow);

        var context = await builder.BuildAsync(
            Repository, 1, state, currentPlan: null, mergeRequest: null,
            Path.Combine(Path.GetTempPath(), "issueagent-context-tests", Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.Equal(
            ["Here is more detail", $"Please inspect this copied marker:\n{CanonicalCommentMarkdown.StateLocatorMarker}"],
            context.PrimaryIssue.HumanComments.Select(comment => comment.Body));
    }

    [Fact]
    public async Task BuildAsyncFollowsRelatedIssuesOneHopByDefault()
    {
        var provider = new FakeGitProvider();
        provider.AddIssue(Repository, 1, "Primary issue", "Primary description");
        provider.AddIssue(Repository, 2, "Related issue", "Related description");
        provider.AddIssue(Repository, 3, "Two hops away", "Should not appear");
        provider.AddRelationship(Repository, 1, "blocks", Repository, 2);
        provider.AddRelationship(Repository, 2, "related", Repository, 3);

        var context = await BuildAsync(provider, 1);

        var related = Assert.Single(context.RelatedIssues);
        Assert.Equal(["blocks"], related.RelationshipPath);
        Assert.Equal(2, related.Issue.Number);
    }

    [Fact]
    public async Task BuildAsyncPreservesFullRelationshipPathThroughMultipleHops()
    {
        var provider = new FakeGitProvider();
        provider.AddIssue(Repository, 1, "Primary issue", "Primary description");
        provider.AddIssue(Repository, 2, "Blocking issue", "Blocking description");
        provider.AddIssue(Repository, 3, "Dependent issue", "Dependent description");
        provider.AddRelationship(Repository, 1, "blocks", Repository, 2);
        provider.AddRelationship(Repository, 2, "blocked-by", Repository, 3);

        var context = await BuildAsync(provider, 1, depth: 2);

        Assert.Equal(
            [($"{Repository.Id}#2", "blocks"), ($"{Repository.Id}#3", "blocks -> blocked-by")],
            context.RelatedIssues.Select(related =>
                ($"{related.Issue.RepositoryId}#{related.Issue.Number}", string.Join(" -> ", related.RelationshipPath))));
    }

    [Fact]
    public async Task BuildAsyncDoesNotReadRelatedIssuesOutsideConfiguredAllowList()
    {
        var provider = new FakeGitProvider();
        provider.AddIssue(Repository, 1, "Primary issue", "Primary description");
        provider.AddRelationship(Repository, 1, "related", UnconfiguredRepository, 2);

        var context = await BuildAsync(provider, 1);

        Assert.Empty(context.RelatedIssues);
    }

    [Theory]
    [InlineData("github/octo/widgets")]
    [InlineData("42")]
    public async Task BuildAsyncReadsConfiguredRelatedRepositoryWhenProviderReportsAlternateId(string providerRepositoryId)
    {
        var provider = new FakeGitProvider();
        var configuredRepository = new RepositoryRef("configured/octo-widgets", Repository.OwnerOrNamespace, Repository.Name);
        var providerRepository = new RepositoryRef(providerRepositoryId, Repository.OwnerOrNamespace, Repository.Name);
        provider.AddIssue(configuredRepository, 1, "Primary issue", "Primary description");
        provider.AddIssue(configuredRepository, 2, "Related issue", "Related description");
        provider.AddRelationship(configuredRepository, 1, "related", providerRepository, 2);

        var context = await BuildAsync(provider, 1, repository: configuredRepository);

        var related = Assert.Single(context.RelatedIssues);
        Assert.Equal(configuredRepository.Id, related.Issue.RepositoryId);
        Assert.Equal(2, related.Issue.Number);
    }

    [Fact]
    public async Task BuildAsyncUsesFirstConfiguredRepositoryWhenProviderNativeIdentityIsDuplicated()
    {
        var provider = new FakeGitProvider();
        var first = new RepositoryRef("configured/octo-widgets-first", Repository.OwnerOrNamespace, Repository.Name);
        var second = new RepositoryRef("configured/octo-widgets-second", "OCTO", "WIDGETS");
        var providerRepository = new RepositoryRef("42", Repository.OwnerOrNamespace, Repository.Name);
        provider.AddIssue(first, 1, "Primary issue", "Primary description");
        provider.AddIssue(first, 2, "Related issue", "Related description");
        provider.AddRelationship(first, 1, "related", providerRepository, 2);

        var context = await BuildAsync(provider, 1, repository: first, allowedRepositories: [first, second]);

        var related = Assert.Single(context.RelatedIssues);
        Assert.Equal(first.Id, related.Issue.RepositoryId);
    }

    [Fact]
    public async Task BuildAsyncDetectsRelationshipCyclesWithoutInfiniteLoop()
    {
        var provider = new FakeGitProvider();
        provider.AddIssue(Repository, 1, "Issue one", "d1");
        provider.AddIssue(Repository, 2, "Issue two", "d2");
        provider.AddRelationship(Repository, 1, "related", Repository, 2);
        provider.AddRelationship(Repository, 2, "related", Repository, 1);

        var context = await BuildAsync(provider, 1, depth: 5);

        var related = Assert.Single(context.RelatedIssues);
        Assert.Equal(2, related.Issue.Number);
    }

    [Fact]
    public async Task BuildAsyncStopsAtZeroTraversalDepth()
    {
        var provider = new FakeGitProvider();
        provider.AddIssue(Repository, 1, "Primary", "d1");
        provider.AddIssue(Repository, 2, "Related", "d2");
        provider.AddRelationship(Repository, 1, "related", Repository, 2);

        var context = await BuildAsync(provider, 1, depth: 0);

        Assert.Empty(context.RelatedIssues);
    }

    [Fact]
    public async Task BuildMergeRequestContextAsyncIncludesCommentsAndReviewThreads()
    {
        var provider = new FakeGitProvider();
        var mergeRequestKey = (Repository.Id, 9L);
        provider.MergeRequestComments[mergeRequestKey] =
        [
            new ProviderComment(1, "bob", "General feedback", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new AttachmentSource("merge-request-comment", "9"), false),
        ];
        provider.ReviewThreads[mergeRequestKey] =
        [
            new ProviderReviewThread("thread-1", false, [
                new ProviderComment(2, "carol", "Please fix this line", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new AttachmentSource("merge-request-review-comment", "9", "thread-1"), false),
            ]),
        ];
        var mergeRequest = new ProviderMergeRequest(Repository, 9, "agent/issue-1", "main", "Fix bug", "Implements the plan", true, false, false, new AttachmentSource("merge-request-description", "9"));
        var builder = new AgentContextBuilder(provider, new AttachmentPipeline(provider, new AttachmentLimits()), new AgentContextBuilderOptions());

        var context = await builder.BuildMergeRequestContextAsync(Repository, mergeRequest, destination, new RemainingBudget(1000), CancellationToken.None);

        Assert.Single(context.Comments);
        var reviewComment = Assert.Single(context.ReviewThreads);
        Assert.Equal("thread-1", reviewComment.ThreadId);
    }

    [Fact]
    public async Task BuildMergeRequestContextAsyncExcludesFlatCommentsDuplicatedByReviewThreads()
    {
        var provider = new FakeGitProvider();
        var mergeRequestKey = (Repository.Id, 9L);
        var timestamp = DateTimeOffset.UtcNow;
        provider.MergeRequestComments[mergeRequestKey] =
        [
            new ProviderComment(1, "bob", "General feedback", timestamp, timestamp, new AttachmentSource("merge-request-comment", "9"), false),
            new ProviderComment(2, "carol", "Please fix this line", timestamp, timestamp, new AttachmentSource("merge-request-comment", "9"), false),
        ];
        provider.ReviewThreads[mergeRequestKey] =
        [
            new ProviderReviewThread("thread-1", false,
            [
                new ProviderComment(2, "carol", "Please fix this line", timestamp, timestamp, new AttachmentSource("merge-request-review-comment", "9", "thread-1"), false),
            ]),
        ];
        var mergeRequest = new ProviderMergeRequest(Repository, 9, "agent/issue-1", "main", "Fix bug", "Implements the plan", true, false, false, new AttachmentSource("merge-request-description", "9"));
        var builder = new AgentContextBuilder(provider, new AttachmentPipeline(provider, new AttachmentLimits()), new AgentContextBuilderOptions());

        var context = await builder.BuildMergeRequestContextAsync(Repository, mergeRequest, destination, new RemainingBudget(1000), CancellationToken.None);

        Assert.Equal([1L], context.Comments.Select(comment => comment.CommentId));
        Assert.Equal([2L], context.ReviewThreads.Select(comment => comment.CommentId));
    }

    [Fact]
    public async Task BuildAsyncReusesAttachmentsAndRetainsBudgetAcrossContextRebuilds()
    {
        var provider = new FakeGitProvider();
        provider.TrustedHosts.Add("github.example");
        provider.AddIssue(Repository, 1, "Issue", "[a](https://github.example/files/a.pdf)");
        provider.DownloadableContent["https://github.example/files/a.pdf"] = new byte[6];
        provider.DownloadableContent["https://github.example/files/b.pdf"] = new byte[6];
        var downloadedUrls = new List<string>();
        provider.AttachmentDownloadOverride = (attachment, directory, maxSize) =>
        {
            downloadedUrls.Add(attachment.Url.ToString());
            var bytes = provider.DownloadableContent[attachment.Url.ToString()];
            Directory.CreateDirectory(directory);
            var path = AttachmentFileNames.ResolveSafeDestination(directory, attachment.SuggestedFileName);
            File.WriteAllBytes(path, bytes);
            return new DownloadedAttachment(path, Path.GetFileName(path), bytes.Length);
        };
        var limits = new AttachmentLimits { MaxTotalSizeBytes = 10 };
        AgentContextBuilder CreateBuilder() => new(provider, new AttachmentPipeline(provider, limits), new AgentContextBuilderOptions
        {
            AttachmentLimits = limits,
            AllowedRepositories = [Repository],
        });
        var state = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planning, WorkflowOperationalState.Working, null,
            0, null, "omp-session", "agent/issue-1", "main", "abc123", DateTimeOffset.UtcNow);

        var first = await CreateBuilder().BuildAsync(Repository, 1, state, null, null, destination, CancellationToken.None);
        provider.Issues[(Repository.Id, 1)] = provider.Issues[(Repository.Id, 1)] with
        {
            Description = "[a](https://github.example/files/a.pdf) [b](https://github.example/files/b.pdf)",
        };
        var rebuilt = await CreateBuilder().BuildAsync(Repository, 1, state, null, null, destination, CancellationToken.None);

        Assert.Equal(1, downloadedUrls.Count(url => url.EndsWith("/a.pdf", StringComparison.Ordinal)));
        Assert.All(rebuilt.PrimaryIssue.Attachments.Where(attachment => attachment.SourceUrl.EndsWith("/a.pdf", StringComparison.Ordinal)), attachment => Assert.False(attachment.IsOmitted));
        Assert.Contains(rebuilt.PrimaryIssue.Attachments, attachment => attachment.SourceUrl.EndsWith("/b.pdf", StringComparison.Ordinal) && attachment.IsOmitted);
        Assert.Single(first.PrimaryIssue.Attachments);
    }

    private static async Task<AgentContext> BuildAsync(
        FakeGitProvider provider,
        long issueNumber,
        int depth = 1,
        RepositoryRef? repository = null,
        IReadOnlyList<RepositoryRef>? allowedRepositories = null)
    {
        var effectiveRepository = repository ?? Repository;
        var options = new AgentContextBuilderOptions
        {
            RelatedIssueTraversalDepth = depth,
            AllowedRepositories = allowedRepositories ?? [effectiveRepository],
        };
        var builder = new AgentContextBuilder(provider, new AttachmentPipeline(provider, options.AttachmentLimits), options);
        var state = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planning, WorkflowOperationalState.Working, null,
            0, null, "omp-session", "agent/issue-1", "main", "abc123", DateTimeOffset.UtcNow);

        return await builder.BuildAsync(
            effectiveRepository, issueNumber, state, currentPlan: null, mergeRequest: null,
            Path.Combine(Path.GetTempPath(), "issueagent-context-tests", Guid.NewGuid().ToString("N")),
            CancellationToken.None);
    }
}
