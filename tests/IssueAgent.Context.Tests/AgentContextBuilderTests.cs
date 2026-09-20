using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Context.Tests;

public sealed class AgentContextBuilderTests
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string destination = Path.Combine(Path.GetTempPath(), "issueagent-context-tests", Guid.NewGuid().ToString("N"));

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
        Assert.Equal("blocks", related.Relationship);
        Assert.Equal(2, related.Issue.Number);
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

    private static async Task<AgentContext> BuildAsync(FakeGitProvider provider, long issueNumber, int depth = 1)
    {
        var options = new AgentContextBuilderOptions { RelatedIssueTraversalDepth = depth };
        var builder = new AgentContextBuilder(provider, new AttachmentPipeline(provider, options.AttachmentLimits), options);
        var state = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planning, WorkflowOperationalState.Working, null,
            0, null, "omp-session", "agent/issue-1", "main", "abc123", DateTimeOffset.UtcNow);

        return await builder.BuildAsync(
            Repository, issueNumber, state, currentPlan: null, mergeRequest: null,
            Path.Combine(Path.GetTempPath(), "issueagent-context-tests", Guid.NewGuid().ToString("N")),
            CancellationToken.None);
    }
}
