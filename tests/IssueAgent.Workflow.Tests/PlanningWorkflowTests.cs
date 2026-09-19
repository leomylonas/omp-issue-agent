using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class PlanningWorkflowTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-workflow-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task RunInitialPlanningAsyncPublishesPlanAndSetsPlannedWaiting()
    {
        provider.AddIssue(Repository, 1, "Null reference on save", "Saving crashes when the title is empty.");
        var omp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Add a guard clause before save.","decisions":["Guard clause chosen over try/catch for clarity."]}"""));

        var workflow = CreateWorkflow();
        var config = CreateConfig() with { SupplementalInstructions = ["Keep public APIs source-compatible."] };

        var outcome = await workflow.RunInitialPlanningAsync(config, 1, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WorkflowOperationalState.Waiting, outcome.State.OperationalState);
        Assert.Equal(WaitingReason.PlanApproval, outcome.State.WaitingReason);
        Assert.Equal(1, outcome.State.PlanRevision);

        var comment = Assert.Single(provider.IssueComments[(Repository.Id, 1)], c => CanonicalCommentMarkdown.IsCanonicalComment(c.Body));
        Assert.Contains("Add a guard clause before save.", comment.Body, StringComparison.Ordinal);
        Assert.Contains("agent:phase:planned", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Contains("agent:state:waiting", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain("agent:phase:planning", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);

        var notification = Assert.Single(notifier.Notifications);
        Assert.Equal(WorkflowNotificationKind.PlanReady, notification.Kind);

        Assert.Single(git.CreatedWorktrees);
        Assert.Contains("Keep public APIs source-compatible.", Assert.Single(omp.RunRequests).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunInitialPlanningAsyncCheckpointsWorkflowBeforeCreatingRetainedWorktree()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        git.OnCreateWorktree = () =>
        {
            var checkpoint = Assert.Single(
                provider.IssueComments[(Repository.Id, 1)],
                comment => CanonicalCommentMarkdown.IsCanonicalComment(comment.Body));
            var state = CanonicalCommentMarkdown.Parse(checkpoint.Body).State;
            Assert.Equal("planning", state.Phase);
            Assert.Equal("working", state.State);
            Assert.Equal("abc123", state.BaseCommit);
        };
        var omp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Plan.","decisions":[]}"""));

        await CreateWorkflow().RunInitialPlanningAsync(CreateConfig(), 1, omp, CancellationToken.None);
    }

    [Fact]
    public async Task RunInitialPlanningAsyncMaterializesLfsWhenRequired()
    {
        provider.AddIssue(Repository, 1, "Add large asset", "Needs an LFS-tracked binary.");
        git.LfsRequired = true;
        var omp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Add the asset via LFS.","decisions":[]}"""));

        var workflow = CreateWorkflow();
        await workflow.RunInitialPlanningAsync(CreateConfig(), 1, omp, CancellationToken.None);

        Assert.Single(git.LfsMaterializedWorktrees);
    }

    [Fact]
    public async Task RunInitialPlanningAsyncFailsWorkflowWhenOmpErrors()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var omp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpErrorEvent("session-1", clock.UtcNow, "OMP crashed", WasCancelled: false));

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunInitialPlanningAsync(CreateConfig(), 1, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(WorkflowPhase.Failed, outcome.State.Phase);
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:failed");
        Assert.Equal(WorkflowNotificationKind.PlanFailed, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task RunInitialPlanningAsyncPersistsFailureWhenOmpResultViolatesContract()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var omp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Missing decisions."}"""));

        var outcome = await CreateWorkflow().RunInitialPlanningAsync(CreateConfig(), 1, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("failed", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State.Phase);
        Assert.Equal(WorkflowNotificationKind.PlanFailed, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task RunInitialPlanningAsyncReconcilesNewCommentsArrivedDuringPlanning()
    {
        provider.AddIssue(Repository, 1, "Bug", "Original description");
        var omp = new FakeOmpClient().EnqueueSessionId("session-1");

        // The comment is injected exactly when the first run starts, i.e. strictly after the
        // planning context was built and strictly before that run's events are observed, so the
        // workflow's post-run reconciliation check is the only thing that can see it.
        omp.EnqueueRun(
            onStart: () => provider.AddComment(Repository, 1, "alice", "Actually please also handle the empty-title case.", clock.UtcNow.AddMinutes(1)),
            new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Initial plan","decisions":[]}"""));
        omp.EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Updated plan covering the empty-title case.","decisions":["Added empty-title handling per human feedback."]}"""));

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunInitialPlanningAsync(CreateConfig(), 1, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        var comment = Assert.Single(provider.IssueComments[(Repository.Id, 1)], c => CanonicalCommentMarkdown.IsCanonicalComment(c.Body));
        Assert.Contains("Updated plan covering the empty-title case.", comment.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunReplanAsyncEditsExistingCommentInPlaceAndIncrementsRevision()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var initialState = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval,
            1, null, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow.AddHours(-1));
        var initialDocument = CanonicalStateSerializer.ToDocument(initialState, null);
        var initialContent = new CanonicalCommentContent("Original plan text.", ["Original decision."], null, initialDocument);
        await provider.CreateIssueCommentAsync(Repository, 1, CanonicalCommentMarkdown.Render(initialContent), CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] = ["agent:phase:planned", "agent:state:waiting", "agent:cmd:replan"];
        provider.AddComment(Repository, 1, "bob", "Please also cover the null case.", clock.UtcNow);

        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Revised plan covering the null case.","decisions":["Added null handling per feedback."]}"""));

        Directory.CreateDirectory(Path.Combine(workspaceRoot, initialState.WorkflowId.ToString(), "worktree"));
        git.LfsRequired = true;

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunReplanAsync(CreateConfig(), 1, initialState, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(2, outcome.State.PlanRevision);
        Assert.Single(provider.CreatedComments);
        Assert.Equal(2, provider.UpdatedComments.Count);
        var updated = provider.UpdatedComments[^1];
        Assert.Contains("Revised plan covering the null case.", updated.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("agent:cmd:replan", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.NotNull(git.CapturedSubmoduleAuthenticationResolver);
        Assert.Contains(Path.Combine(workspaceRoot, initialState.WorkflowId.ToString(), "worktree"), git.LfsMaterializedWorktrees);
    }
    [Fact]
    public async Task RunReplanAsyncPersistsFailureWhenOmpResultViolatesContract()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var state = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval,
            1, null, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow.AddHours(-1));
        await provider.CreateIssueCommentAsync(
            Repository,
            1,
            CanonicalCommentMarkdown.Render(new CanonicalCommentContent(
                "Original plan.", [], null, CanonicalStateSerializer.ToDocument(state, null))),
            CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            [WorkflowLabels.PlannedPhase, WorkflowLabels.WaitingState, WorkflowCommandLabels.Replan];
        Directory.CreateDirectory(Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree"));

        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Missing decisions."}"""));

        var outcome = await CreateWorkflow().RunReplanAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(WorkflowPhase.Failed, outcome.State.Phase);
        var persisted = CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body);
        Assert.Equal("failed", persisted.State.Phase);
        Assert.Equal(WorkflowNotificationKind.PlanFailed, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task RunInitialPlanningAsyncScopesSubmoduleCredentialsByConfiguredHostOnly()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var omp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Plan.","decisions":[]}"""));
        var trustedAuthentication = new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "trusted-token" };
        var config = CreateConfig() with
        {
            SubmoduleAuthenticationResolver = host => host == "git.trusted.example" ? trustedAuthentication : null,
        };

        await CreateWorkflow().RunInitialPlanningAsync(config, 1, omp, CancellationToken.None);

        var resolver = git.CapturedSubmoduleAuthenticationResolver;
        Assert.NotNull(resolver);
        Assert.Same(trustedAuthentication, resolver!("git.trusted.example"));
        Assert.Null(resolver("attacker.example"));
    }

    [Fact]
    public async Task RunInitialPlanningAsyncNeverForwardsRepositoryCredentialsToUnconfiguredSubmoduleHosts()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var omp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Plan.","decisions":[]}"""));
        var config = CreateConfig() with
        {
            GitAuthentication = new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "repo-token" },
        };

        await CreateWorkflow().RunInitialPlanningAsync(config, 1, omp, CancellationToken.None);

        var resolver = git.CapturedSubmoduleAuthenticationResolver;
        Assert.NotNull(resolver);
        Assert.Null(resolver!("any-unconfigured-host.example"));
    }

    private PlanningWorkflow CreateWorkflow()
    {
        var attachmentPipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        var contextBuilder = new AgentContextBuilder(provider, attachmentPipeline, new AgentContextBuilderOptions());
        var deps = new WorkflowDependencies(provider, git, contextBuilder, notifier, clock);
        return new PlanningWorkflow(deps);
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
