using IssueAgent.Domain;

namespace IssueAgent.Git.Tests;

public sealed class RetryingGitRepositoryManagerTests
{
    private static readonly GitAuthentication authentication = TempGitFixtures.AnonymousAuthentication();
    private static readonly RetryPolicy retryPolicy = new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.Zero,
        MaxJitter = TimeSpan.Zero,
    };

    [Fact]
    public async Task SafeRemoteOperationsRetryTransientFailures()
    {
        var inner = new TransientFailingGitRepositoryManager { FailuresPerOperation = 1 };
        var manager = new RetryingGitRepositoryManager(inner, retryPolicy);

        await manager.EnsureBareRepositoryAsync("repo", "https://example.test/repo.git", authentication, CancellationToken.None);
        await manager.FetchAsync("repo", authentication, CancellationToken.None);
        await manager.UpdateSubmodulesAsync("repo", "/tmp/worktree", _ => authentication, CancellationToken.None);
        await manager.MaterializeLfsContentAsync("repo", "/tmp/worktree", authentication, _ => authentication, CancellationToken.None);
        await manager.UploadLfsObjectsAsync("repo", "/tmp/worktree", "agent/issue-1", authentication, CancellationToken.None);

        Assert.Equal(2, inner.Calls["clone"]);
        Assert.Equal(2, inner.Calls["fetch"]);
        Assert.Equal(2, inner.Calls["submodule"]);
        Assert.Equal(2, inner.Calls["lfs-pull"]);
        Assert.Equal(2, inner.Calls["lfs-push"]);
    }

    [Fact]
    public async Task PushIsNotRetriedWhenItsRemoteOutcomeIsAmbiguous()
    {
        var inner = new TransientFailingGitRepositoryManager { FailuresPerOperation = 1 };
        var manager = new RetryingGitRepositoryManager(inner, retryPolicy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PushAsync("repo", "/tmp/worktree", "agent/issue-1", authentication, CancellationToken.None).AsTask());

        Assert.Equal(1, inner.Calls["push"]);
    }

    [Fact]
    public async Task CloneRetryCanRecoverFromAnIncompleteDestination()
    {
        var reposRoot = TempGitFixtures.CreateTempDirectory();
        var remote = TempGitFixtures.CreateRemoteRepositoryWithCommit(out _);
        try
        {
            Directory.CreateDirectory(Path.Combine(reposRoot, "repo"));
            var manager = new LibGit2SharpRepositoryManager(reposRoot);

            await manager.EnsureBareRepositoryAsync("repo", remote, authentication, CancellationToken.None);

            Assert.True(LibGit2Sharp.Repository.IsValid(Path.Combine(reposRoot, "repo")));
        }
        finally
        {
            Directory.Delete(reposRoot, recursive: true);
            Directory.Delete(remote, recursive: true);
        }
    }

    private sealed class TransientFailingGitRepositoryManager : IGitRepositoryManager
    {
        public int FailuresPerOperation { get; init; }
        public Dictionary<string, int> Calls { get; } = [];

        public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication auth, CancellationToken cancellationToken) => Attempt("clone");
        public ValueTask FetchAsync(string repositoryId, GitAuthentication auth, CancellationToken cancellationToken) => Attempt("fetch");
        public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => ValueTask.FromResult("abc123");
        public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("abc123");
        public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RenameWorktreeBranchAsync(string repositoryId, string worktreePath, string expectedCurrentBranch, string newBranchName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult("abc123");
        public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) => Attempt("submodule");
        public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication auth, CancellationToken cancellationToken) => Attempt("push");
        public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public bool WorktreeRequiresLfs(string worktreePath) => false;
        public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication auth, Func<string, GitAuthentication?> submoduleAuthenticationResolver, CancellationToken cancellationToken) => Attempt("lfs-pull");
        public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication auth, CancellationToken cancellationToken) => Attempt("lfs-push");

        private ValueTask Attempt(string operation)
        {
            var calls = Calls.GetValueOrDefault(operation) + 1;
            Calls[operation] = calls;
            if (calls <= FailuresPerOperation)
            {
                throw new InvalidOperationException($"Transient {operation} failure.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
