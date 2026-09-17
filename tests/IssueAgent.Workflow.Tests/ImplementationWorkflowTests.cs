using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class ImplementationWorkflowTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-impl-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task RunAsyncInPlanOnlyModeRejectsImplementCommandAndRestoresWaiting()
    {
        var state = await SeedApprovedPlanAsync();
        var workflow = CreateWorkflow();

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.PlanOnly, 1, state, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WaitingReason.PlanApproval, outcome.State.WaitingReason);
        Assert.Empty(provider.UpdatedComments);
    }

    [Fact]
    public async Task RunAsyncPublishesDraftMergeRequestAndSetsReviewWaiting()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Added a guard clause.","keyChanges":["Guard clause in Save()"],"decisions":["Chose guard clause for clarity."],"checksRun":["dotnet test"],"knownFailures":[],"deviations":[],"risks":[]}
            """));

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(WaitingReason.ReviewRequested, outcome.State.WaitingReason);
        Assert.Single(provider.MergeRequests);
        Assert.True(provider.MergeRequests[1].IsDraft);
        var updated = Assert.Single(provider.UpdatedComments);
        Assert.Contains("Added a guard clause.", updated.Body, StringComparison.Ordinal);
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:review");
        Assert.Equal(WorkflowNotificationKind.ImplementationReady, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task RunAsyncPausesForMaterialDeviationWithoutPublishing()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Discovered a bigger issue.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"The fix requires a schema migration not covered by the plan."}
            """));

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.MaterialPlanDeviation, outcome.State.WaitingReason);
        Assert.Empty(provider.MergeRequests);
        Assert.Contains("schema migration", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncRunsCorrectivePassWhenChangesAreUncommitted()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"First pass.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Committed remaining changes.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var uncommittedGit = new UncommittedThenCleanGitManager(git);
        var deps = new WorkflowDependencies(provider, uncommittedGit, CreateContextBuilder(), notifier, clock);
        var workflow = new ImplementationWorkflow(deps);

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(2, omp.RunRequests.Count);
        var updated = Assert.Single(provider.UpdatedComments);
        Assert.Contains("Committed remaining changes.", updated.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncBlocksImplementationWhenPlanIsStale()
    {
        var state = await SeedApprovedPlanAsync();
        provider.AddIssue(Repository, 1, "Bug", "Description changed after approval", null);
        provider.Issues[(Repository.Id, 1)] = provider.Issues[(Repository.Id, 1)] with { UpdatedAt = clock.UtcNow.AddHours(1) };

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WaitingReason.ReplanRequired, outcome.State.WaitingReason);
        Assert.Empty(provider.MergeRequests);
    }

    private async Task<WorkflowState> SeedApprovedPlanAsync()
    {
        provider.AddIssue(Repository, 1, "Bug", "Original description");
        var workflowId = WorkflowId.New();
        var state = new WorkflowState(
            workflowId, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval,
            1, 1, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow.AddHours(-1));
        var document = CanonicalStateSerializer.ToDocument(state, null);
        var content = new CanonicalCommentContent("Approved plan text.", ["Decision one."], null, document);
        await provider.CreateIssueCommentAsync(Repository, 1, CanonicalCommentMarkdown.Render(content), CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] = ["agent:phase:planned", "agent:state:waiting", "agent:cmd:implement"];
        Directory.CreateDirectory(Path.Combine(workspaceRoot, workflowId.ToString(), "worktree"));
        return state;
    }

    private ImplementationWorkflow CreateWorkflow() => new(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

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

    /// <summary>Wraps a real <see cref="FakeGitRepositoryManager"/> but reports uncommitted changes
    /// exactly once, so the workflow's corrective-pass path is exercised deterministically.</summary>
    private sealed class UncommittedThenCleanGitManager(FakeGitRepositoryManager inner) : IGitRepositoryManager
    {
        private bool reported;

        public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) => inner.EnsureBareRepositoryAsync(repositoryId, cloneUrl, authentication, cancellationToken);
        public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) => inner.FetchAsync(repositoryId, authentication, cancellationToken);
        public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.ResolveBranchCommitAsync(repositoryId, branchName, cancellationToken);
        public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) => inner.CreateWorktreeAsync(repositoryId, worktreeId, worktreePath, branchName, baseCommit, cancellationToken);
        public ValueTask ResetWorktreeAsync(string worktreePath, string commit, CancellationToken cancellationToken) => inner.ResetWorktreeAsync(worktreePath, commit, cancellationToken);

        public ValueTask<bool> HasUncommittedChangesAsync(string worktreePath, CancellationToken cancellationToken)
        {
            if (!reported)
            {
                reported = true;
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }

        public ValueTask<string> GetHeadCommitAsync(string worktreePath, CancellationToken cancellationToken) => inner.GetHeadCommitAsync(worktreePath, cancellationToken);
        public ValueTask UpdateSubmodulesAsync(string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) => inner.UpdateSubmodulesAsync(worktreePath, authenticationResolver, cancellationToken);
        public ValueTask<bool> TryRebaseOntoAsync(string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => inner.TryRebaseOntoAsync(worktreePath, ontoCommit, identity, cancellationToken);
        public ValueTask<bool> TryMergeAsync(string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => inner.TryMergeAsync(worktreePath, commit, identity, cancellationToken);
        public ValueTask PushAsync(string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => inner.PushAsync(worktreePath, branchName, authentication, cancellationToken);
        public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) => inner.RemoveWorktreeAsync(repositoryId, worktreeId, worktreePath, cancellationToken);
        public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.RemoveLocalBranchAsync(repositoryId, branchName, cancellationToken);
        public bool WorktreeRequiresLfs(string worktreePath) => inner.WorktreeRequiresLfs(worktreePath);
        public ValueTask MaterializeLfsContentAsync(string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken) => inner.MaterializeLfsContentAsync(worktreePath, authentication, cancellationToken);
        public ValueTask UploadLfsObjectsAsync(string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => inner.UploadLfsObjectsAsync(worktreePath, branchName, authentication, cancellationToken);
    }
}
