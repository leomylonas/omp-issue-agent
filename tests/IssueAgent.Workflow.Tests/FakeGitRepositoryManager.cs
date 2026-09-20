using IssueAgent.Git;

namespace IssueAgent.Workflow.Tests;

/// <summary>In-memory <see cref="IGitRepositoryManager"/> for workflow orchestration tests. Creates
/// real directories (so worktree paths exist) but performs no real Git operations; real Git
/// plumbing is covered by IssueAgent.Git.Tests.</summary>
public sealed class FakeGitRepositoryManager : IGitRepositoryManager
{
    public string BranchCommitToReturn { get; set; } = "abc123";

    public List<(string WorktreeId, string BranchName, string BaseCommit)> CreatedWorktrees { get; } = [];

    public Action? OnCreateWorktree { get; set; }

    public bool LfsRequired { get; set; }

    public bool MergeSucceeds { get; set; } = true;

    public int MergeAttempts { get; private set; }

    public List<string> LfsMaterializedWorktrees { get; } = [];

    public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(BranchCommitToReturn);

    public string? RemoteBranchCommitToReturn { get; set; } = "abc123";

    public bool RemoteBranchIsDescendant { get; set; } = true;

    public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RemoteBranchCommitToReturn);

    public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RemoteBranchIsDescendant);

    public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken)
    {
        OnCreateWorktree?.Invoke();
        Directory.CreateDirectory(worktreePath);
        CreatedWorktrees.Add((worktreeId, branchName, baseCommit));
        return ValueTask.CompletedTask;
    }

    public int ResetWorktreeCallCount { get; private set; }

    public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken)
    {
        ResetWorktreeCallCount++;
        return ValueTask.CompletedTask;
    }

    public bool WorktreeHasUncommittedChanges { get; set; }

    public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult(WorktreeHasUncommittedChanges);

    public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult(BranchCommitToReturn);

    public Func<string, GitAuthentication?>? CapturedSubmoduleAuthenticationResolver { get; private set; }

    public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken)
    {
        CapturedSubmoduleAuthenticationResolver = authenticationResolver;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken)
    {
        MergeAttempts++;
        return ValueTask.FromResult(MergeSucceeds);
    }

    public int PushCallCount { get; private set; }

    public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        PushCallCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken)
    {
        if (Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public bool WorktreeRequiresLfs(string worktreePath) => LfsRequired;

    public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        LfsMaterializedWorktrees.Add(worktreePath);
        return ValueTask.CompletedTask;
    }

    public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;

}
