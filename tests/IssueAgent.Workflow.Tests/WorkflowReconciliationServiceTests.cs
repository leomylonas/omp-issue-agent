using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class WorkflowReconciliationServiceTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-reconciliation-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task ConsistentWaitingStateAllowsCommandDispatchWithoutRemoteMutation()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.ResumeAllowed, result.Disposition);
        Assert.Equal(state, result.State);
        Assert.Empty(provider.UpdatedComments);
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task PersistedWorkingStateIsPausedAndEscalatedWithoutDeletingWorktree()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Implementing, WorkflowOperationalState.Working, waitingReason: null);
        var worktreePath = WorktreePath(state);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.ManualIntervention, result.State!.WaitingReason);
        Assert.True(Directory.Exists(worktreePath));
        Assert.Contains("agent:state:waiting", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Single(provider.UpdatedComments);
        Assert.Equal(WorkflowNotificationKind.HumanActionRequired, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task DivergedRemoteBranchIsPausedAndLocalStateIsPreserved()
    {
        git.BranchCommitToReturn = "local-sha";
        git.RemoteBranchCommitToReturn = "remote-sha";
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Implementing, WorkflowOperationalState.Waiting, WaitingReason.ManualIntervention);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.RemoteHistoryRewrite, result.State!.WaitingReason);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Single(provider.UpdatedComments);
    }

    [Fact]
    public async Task MergedRequestTransitionsDoneAndCleansOnlyLocalState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);
        provider.MergeRequests[7] = new ProviderMergeRequest(
            Repository,
            7,
            state.Branch,
            state.TargetBranch,
            "Fix",
            "Description",
            IsDraft: false,
            IsMerged: true,
            IsClosed: true,
            new AttachmentSource("merge-request-description", "7"));

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Completed, result.Disposition);
        Assert.Equal(WorkflowPhase.Done, result.State!.Phase);
        Assert.False(Directory.Exists(WorktreePath(state)));
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("done", persisted.State.Phase);
    }

    [Fact]
    public async Task ClosedUnmergedRequestTransitionsCancelledAndCleansOnlyLocalState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);
        provider.MergeRequests[7] = new ProviderMergeRequest(
            Repository,
            7,
            state.Branch,
            state.TargetBranch,
            "Fix",
            "Description",
            IsDraft: false,
            IsMerged: false,
            IsClosed: true,
            new AttachmentSource("merge-request-description", "7"));

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Completed, result.Disposition);
        Assert.Equal(WorkflowPhase.Cancelled, result.State!.Phase);
        Assert.False(Directory.Exists(WorktreePath(state)));
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("cancelled", persisted.State.Phase);
    }

    [Fact]
    public async Task AmbiguousLabelsPauseWithoutDeletingRetainedState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowLabels.ReviewPhase);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.AmbiguousCommand, result.State!.WaitingReason);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Single(provider.UpdatedComments);
        Assert.Single(notifier.Notifications);
    }

    [Fact]
    public async Task CorruptCanonicalStateEscalatesWithoutDeletingRetainedState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Working, waitingReason: null);
        var corrupt = canonical with { Body = CanonicalCommentMarkdown.StateLocatorMarker };

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, corrupt, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Corrupt, result.Disposition);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Contains("agent:state:waiting", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain("agent:state:working", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        var update = Assert.Single(provider.UpdatedComments);
        Assert.Contains("IssueAgent paused", update.Body, StringComparison.Ordinal);
        Assert.Equal(WorkflowNotificationKind.HumanActionRequired, Assert.Single(notifier.Notifications).Kind);
    }

    private (WorkflowState State, ProviderComment Canonical) SeedWorkflow(
        WorkflowPhase phase,
        WorkflowOperationalState operationalState,
        WaitingReason? waitingReason)
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var state = new WorkflowState(
            WorkflowId.New(),
            phase,
            operationalState,
            waitingReason,
            1,
            1,
            "session-1",
            "agent/issue-1-bug",
            "main",
            "abc123",
            clock.UtcNow.AddHours(-1),
            PlanInputHash: PlanInputHasher.Compute("Bug", "Description"));
        Directory.CreateDirectory(WorktreePath(state));
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            [WorkflowLabels.Phase(phase), WorkflowLabels.State(operationalState)];
        var content = new CanonicalCommentContent(
            "Implement the fix.",
            ["Keep the change focused."],
            null,
            CanonicalStateSerializer.ToDocument(state, phase == WorkflowPhase.Review ? $"{Repository.Id}#7" : null));
        provider.AddComment(Repository, 1, "issue-agent", CanonicalCommentMarkdown.Render(content), clock.UtcNow, isBot: true);
        return (state, provider.IssueComments[(Repository.Id, 1)].Single());
    }

    private WorkflowReconciliationService CreateService() => new(
        new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

    private AgentContextBuilder CreateContextBuilder() => new(
        provider,
        new AttachmentPipeline(provider, new AttachmentLimits()),
        new AgentContextBuilderOptions());

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

    private string WorktreePath(WorkflowState state) =>
        Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");

    public void Dispose()
    {
        if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
    }
}
