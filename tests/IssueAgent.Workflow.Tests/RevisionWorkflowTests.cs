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

        var checkpoints = provider.UpdatedComments;
        Assert.Equal(3, checkpoints.Count);
        Assert.Contains("phase: revising", checkpoints[0].Body, StringComparison.Ordinal);
        Assert.Contains("state: working", checkpoints[0].Body, StringComparison.Ordinal);
        Assert.Contains("Renamed the variable.", checkpoints[1].Body, StringComparison.Ordinal);
        Assert.Contains("Renamed the variable.", checkpoints[2].Body, StringComparison.Ordinal);
        var checkpoint = CanonicalCommentMarkdown.Parse(checkpoints[1].Body);
        Assert.Equal("revising", checkpoint.State.Phase);
        Assert.Equal("working", checkpoint.State.State);
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:review");
        Assert.Equal(clock.UtcNow, CanonicalCommentMarkdown.Parse(checkpoints[2].Body).State.ReviewFeedbackCutoff);
        Assert.DoesNotContain("agent:cmd:revise", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
    }

    [Fact]
    public async Task RunAsyncPersistsFailureWhenOmpResultViolatesContract()
    {
        var state = await SeedReviewStateAsync();
        var omp = new FakeOmpClient().EnqueueRun(
            new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Missing required fields."}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Null(CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult);
        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("failed", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State.Phase);
        Assert.Equal(WorkflowNotificationKind.RevisionFailed, Assert.Single(notifier.Notifications).Kind);
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
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Conflict resolved.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));
        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(
                CreateConfig() with { RevisionRole = "reviewer", ConflictResolutionRole = "resolver" },
                1,
                state,
                omp,
                CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(1, git.MergeAttempts);
        Assert.Equal(2, omp.RunRequests.Count);
        Assert.All(omp.RunRequests, request => Assert.Equal("session-1", request.SessionId));
        Assert.Equal(["reviewer", "resolver"], omp.SelectedRoles);
        Assert.Contains("conflict", omp.RunRequests[1].Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsyncPausesForMaterialDeviationBeforeMergeOrPush()
    {
        var state = await SeedReviewStateAsync();
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Needs architecture change.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["Requires a new service boundary."],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"The approved plan cannot safely support the required service boundary."}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.MaterialPlanDeviation, outcome.State.WaitingReason);
        Assert.Equal(0, git.MergeAttempts);
        Assert.Contains("service boundary", Assert.Single(notifier.Notifications).Message, StringComparison.Ordinal);
        Assert.Contains("Needs architecture change.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncKeepsRevisionCheckpointAndUsesDistinctReasonWhenNewFeedbackArrives()
    {
        var state = await SeedReviewStateAsync();
        var omp = new FakeOmpClient();
        omp.EnqueueRun(
            onStart: () => provider.MergeRequestComments[(Repository.Id, 1)] =
            [
                new ProviderComment(9, "bob", "Please account for this new feedback.", clock.UtcNow.AddMinutes(1), clock.UtcNow.AddMinutes(1),
                    new AttachmentSource("merge-request-comment", "9"), false),
            ],
            new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Revising, outcome.State.Phase);
        Assert.Equal(WaitingReason.NewFeedbackDuringRevision, outcome.State.WaitingReason);
        Assert.Contains("Revision.", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult, StringComparison.Ordinal);
        Assert.Equal(0, git.PushCallCount);
    }

    [Fact]
    public async Task RunAsyncFailsClosedWhenReviewFeedbackCheckpointIsMissing()
    {
        var state = await SeedReviewStateAsync();
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var content = CanonicalCommentMarkdown.Parse(canonical.Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(content with { State = content.State with { ReviewFeedbackCutoff = null } }),
            CancellationToken.None);
        var omp = new FakeOmpClient();

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.CorruptState, outcome.State.WaitingReason);
        Assert.Empty(omp.RunRequests);
        Assert.Contains("feedback checkpoint", Assert.Single(notifier.Notifications).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncContinueAfterMaterialDeviationPublishesRetainedRevisionWithoutRerunningOmp()
    {
        var state = await SeedReviewStateAsync();
        var materialDeviation = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Needs architecture change.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["Requires a new service boundary."],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"The approved plan cannot safely support the required service boundary."}"""));
        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

        var paused = await workflow.RunAsync(CreateConfig(), 1, state, materialDeviation, CancellationToken.None);
        var continued = await workflow.RunAsync(CreateConfig(), 1, paused.State, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, continued.State.Phase);
        Assert.Equal(WaitingReason.ReviewRequested, continued.State.WaitingReason);
        Assert.Equal(1, git.PushCallCount);
        Assert.Contains("Needs architecture change.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }

    private async Task<WorkflowState> SeedReviewStateAsync()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var workflowId = WorkflowId.New();
        var state = new WorkflowState(
            workflowId, WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested,
            1, 1, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow,
            ReviewFeedbackCutoff: clock.UtcNow.AddHours(-1));
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
