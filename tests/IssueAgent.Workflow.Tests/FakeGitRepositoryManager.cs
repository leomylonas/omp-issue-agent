using IssueAgent.Git;

namespace IssueAgent.Workflow.Tests;

/// <summary>In-memory <see cref="IGitRepositoryManager"/> for workflow orchestration tests. Creates
/// real directories (so worktree paths exist) but performs no real Git operations; real Git
/// plumbing is covered by IssueAgent.Git.Tests.</summary>
public sealed class FakeGitRepositoryManager : IGitRepositoryManager
{
    public string BranchCommitToReturn { get; set; } = "abc123";

    public List<(string WorktreeId, string BranchName, string BaseCommit)> CreatedWorktrees { get; } = [];

    public bool LfsRequired { get; set; }

    public List<string> LfsMaterializedWorktrees { get; } = [];

    public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(BranchCommitToReturn);

    public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(worktreePath);
        CreatedWorktrees.Add((worktreeId, branchName, baseCommit));
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetWorktreeAsync(string worktreePath, string commit, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<bool> HasUncommittedChangesAsync(string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<string> GetHeadCommitAsync(string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult(BranchCommitToReturn);

    public ValueTask UpdateSubmodulesAsync(string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<bool> TryRebaseOntoAsync(string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask<bool> TryMergeAsync(string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask PushAsync(string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;

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

    public ValueTask MaterializeLfsContentAsync(string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        LfsMaterializedWorktrees.Add(worktreePath);
        return ValueTask.CompletedTask;
    }

    public ValueTask UploadLfsObjectsAsync(string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
