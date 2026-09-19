using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Providers;
using IssueAgent.Workflow;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class WorkflowCommandRoutingTests
{
    [Fact]
    public void ResolvePausesForMergeRequestAmbiguityEvenWhenIssueHasCommand()
    {
        var issue = LabelProtocol.Analyze(
            [WorkflowLabels.PlannedPhase, WorkflowLabels.WaitingState, WorkflowCommandLabels.Replan]);
        var mergeRequest = LabelProtocol.Analyze(
            [WorkflowCommandLabels.Revise, WorkflowCommandLabels.Cancel]);

        var result = WorkflowCommandRouting.Resolve(issue, mergeRequest);

        Assert.True(result.IsAmbiguous);
        Assert.Null(result.Command);
    }

    [Fact]
    public void ResolveIgnoresImplementAndReplanLabelsOnMergeRequests()
    {
        var issue = LabelProtocol.Analyze([]);

        foreach (var unsupportedCommand in new[] { WorkflowCommandLabels.Implement, WorkflowCommandLabels.Replan })
        {
            var result = WorkflowCommandRouting.Resolve(issue, LabelProtocol.Analyze([unsupportedCommand]));

            Assert.False(result.IsAmbiguous);
            Assert.Null(result.Command);
            Assert.Equal(WorkflowCommandSource.None, result.Sources);
        }
    }

    [Fact]
    public void ResolveFiltersUnsupportedMergeRequestCommandsBeforeCheckingAmbiguity()
    {
        var issue = LabelProtocol.Analyze([]);
        var mergeRequest = LabelProtocol.Analyze([WorkflowCommandLabels.Replan, WorkflowCommandLabels.Revise]);

        var result = WorkflowCommandRouting.Resolve(issue, mergeRequest);

        Assert.False(result.IsAmbiguous);
        Assert.Equal(WorkflowCommand.Revise, result.Command);
        Assert.Equal(WorkflowCommandSource.MergeRequest, result.Sources);
    }

    [Fact]
    public void ContinueRouteAdoptsPublishedReviewWithoutStartingRevision()
    {
        var review = CreateState(WorkflowPhase.Review, WorkflowOperationalState.Waiting);

        var route = WorkflowCommandRouting.ContinueRoute(review, review);

        Assert.Null(route);
    }

    [Fact]
    public void ContinueRouteResumesOnlyDurablyInterruptedRevision()
    {
        var pausedRevision = CreateState(WorkflowPhase.Revising, WorkflowOperationalState.Waiting);
        var interruptedRevision = CreateState(WorkflowPhase.Revising, WorkflowOperationalState.Working);

        Assert.Null(WorkflowCommandRouting.ContinueRoute(pausedRevision, pausedRevision));
        Assert.Equal(WorkflowCommand.Revise, WorkflowCommandRouting.ContinueRoute(pausedRevision, interruptedRevision));
    }

    [Fact]
    public void ContinueRouteResumesInterruptedPlanningAsReplan()
    {
        var pausedPlanning = CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Waiting);
        var interruptedPlanning = CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Working);

        Assert.Null(WorkflowCommandRouting.ContinueRoute(pausedPlanning, pausedPlanning));
        Assert.Equal(WorkflowCommand.Replan, WorkflowCommandRouting.ContinueRoute(pausedPlanning, interruptedPlanning));
    }

    [Fact]
    public void ContinueRouteReplansFromDurablyRecordedInterruptedPlanning()
    {
        var recoveredPlanning = CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Waiting) with
        {
            InterruptedPhase = WorkflowPhase.Planning,
        };

        Assert.Equal(WorkflowCommand.Replan, WorkflowCommandRouting.ContinueRoute(recoveredPlanning, recoveredPlanning));
    }

    [Fact]
    public async Task ReviewContinueRecoveryResetsCleanRetainedWorkspaceToRemoteHead()
    {
        var state = ReviewState();
        var root = Path.Combine(Path.GetTempPath(), $"issue-agent-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(root, state.WorkflowId.ToString(), "worktree");
        Directory.CreateDirectory(worktreePath);
        try
        {
            var git = new ContinueRecoveryGit { RemoteHead = "remote-rewrite" };

            await WorkflowDispatcher.RecoverContinueWorkspaceAsync(
                Dependencies(git), Config(root), state, CancellationToken.None);

            Assert.Equal([("repo", worktreePath, "remote-rewrite")], git.ResetWorktrees);
            Assert.Empty(git.CreatedWorktrees);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReviewContinueRecoveryRecreatesMissingWorkspaceAtRemoteHead()
    {
        var state = ReviewState();
        var root = Path.Combine(Path.GetTempPath(), $"issue-agent-{Guid.NewGuid():N}");
        try
        {
            var git = new ContinueRecoveryGit { RemoteHead = "remote-rewrite" };

            await WorkflowDispatcher.RecoverContinueWorkspaceAsync(
                Dependencies(git), Config(root), state, CancellationToken.None);

            Assert.Equal([(state.WorkflowId.ToString(), "agent/issue-1", "remote-rewrite")], git.CreatedWorktrees);
            Assert.Empty(git.ResetWorktrees);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WorkflowState ReviewState() => CreateState(WorkflowPhase.Review, WorkflowOperationalState.Waiting) with
    {
        WorkflowId = new WorkflowId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
    };

    private static WorkflowDependencies Dependencies(IGitRepositoryManager git) =>
        new(null!, git, null!, new NullWorkflowNotifier(), new SystemClock());

    private static WorkflowRepositoryConfig Config(string root) => new(
        new RepositoryRef("repo", "owner", "name"),
        Path.Combine(root, "repos"),
        root,
        "main",
        GitAuthentication.Anonymous(TlsTrust.System),
        new GitIdentity("IssueAgent", "issue-agent@example.test"),
        "omp",
        [],
        new Dictionary<string, string>(),
        "plan",
        "task");

    private sealed class ContinueRecoveryGit : IGitRepositoryManager
    {
        public string? RemoteHead { get; init; }

        public List<(string WorktreeId, string Branch, string BaseCommit)> CreatedWorktrees { get; } = [];

        public List<(string RepositoryId, string WorktreePath, string Commit)> ResetWorktrees { get; } = [];

        public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(RemoteHead);

        public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken)
        {
            CreatedWorktrees.Add((worktreeId, branchName, baseCommit));
            Directory.CreateDirectory(worktreePath);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken)
        {
            ResetWorktrees.Add((repositoryId, worktreePath, commit));
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool WorktreeRequiresLfs(string worktreePath) => throw new NotSupportedException();
        public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static WorkflowState CreateState(WorkflowPhase phase, WorkflowOperationalState operationalState) => new(
        WorkflowId.New(),
        phase,
        operationalState,
        operationalState == WorkflowOperationalState.Waiting ? WaitingReason.ManualIntervention : null,
        PlanRevision: 1,
        ApprovedPlanRevision: 1,
        OmpSessionId: "session-1",
        Branch: "agent/issue-1",
        TargetBranch: "main",
        BaseCommit: "abc123",
        UpdatedAt: DateTimeOffset.UtcNow,
        PlanInputHash: "hash");
}
