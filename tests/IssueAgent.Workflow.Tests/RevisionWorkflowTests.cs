using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class RevisionWorkflowTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-revision-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task RunAsyncCollectsReviewFeedbackAndRepublishesInPlace()
    {
        var state = await SeedReviewStateAsync();
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "bob", "Please rename this variable.", clock.UtcNow, clock.UtcNow, new AttachmentSource("merge-request-comment", "1"), false),
        ];
        provider.ReviewThreads[(Repository.Id, 1)] =
        [
            new ProviderReviewThread(
                "thread-1",
                IsResolved: true,
                [
                    new ProviderComment(2, "carol", "This concern is resolved.", clock.UtcNow, clock.UtcNow,
                        new AttachmentSource("review-thread-comment", "2"), false),
                ]),
        ];

        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Renamed the variable.","keyChanges":["Renamed x to itemCount"],"decisions":[],"checksRun":["dotnet test"],"knownFailures":[],"deviations":[],"risks":[]}
            """));

        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(WaitingReason.ReviewRequested, outcome.State.WaitingReason);

        var request = Assert.Single(omp.RunRequests);
        Assert.Contains("Please rename this variable.", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("resolved: true", request.Prompt, StringComparison.Ordinal);

        var updated = Assert.Single(provider.UpdatedComments);
        Assert.Contains("Renamed the variable.", updated.Body, StringComparison.Ordinal);
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:review");
        Assert.DoesNotContain("agent:cmd:revise", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
    }


    [Fact]
    public async Task RunAsyncUsesSameOmpSessionToResolveTargetConflictBeforePush()
    {
        var state = await SeedReviewStateAsync();
        git.MergeSucceeds = false;
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Conflict resolved."}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(1, git.MergeAttempts);
        Assert.Equal(2, omp.RunRequests.Count);
        Assert.All(omp.RunRequests, request => Assert.Equal("session-1", request.SessionId));
        Assert.Contains("conflict", omp.RunRequests[1].Prompt, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WorkflowState> SeedReviewStateAsync()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var workflowId = WorkflowId.New();
        var state = new WorkflowState(
            workflowId, WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested,
            1, 1, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow.AddHours(-1));
        var document = CanonicalStateSerializer.ToDocument(state, "github/octo/widgets#1");
        var content = new CanonicalCommentContent("Plan text.", ["Decision."], "Initial implementation summary.", document);
        await provider.CreateIssueCommentAsync(Repository, 1, CanonicalCommentMarkdown.Render(content), CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] = ["agent:phase:review", "agent:state:waiting", "agent:cmd:revise"];
        provider.MergeRequests[1] = new ProviderMergeRequest(Repository, 1, state.Branch, state.TargetBranch, "Bug", "body", true, false, false, new AttachmentSource("merge-request-description", "1"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, workflowId.ToString(), "worktree"));
        return state;
    }

    private AgentContextBuilder CreateContextBuilder()
    {
        var attachmentPipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        return new AgentContextBuilder(provider, attachmentPipeline, new AgentContextBuilderOptions());
    }

    private WorkflowRepositoryConfig CreateConfig() => new(
        Repository,
        Path.Combine(workspaceRoot, "repo"),
        workspaceRoot,
        "main",
        GitAuthentication.Anonymous(TlsTrust.System),
        new GitIdentity("IssueAgent", "issue-agent@example.com"),
        "/usr/local/bin/omp",
        [],
        new Dictionary<string, string>(),
        "plan",
        "task");

    public void Dispose()
    {
        if (Directory.Exists(workspaceRoot))
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }
}
