using System.Runtime.ExceptionServices;
using IssueAgent.Domain;

namespace IssueAgent.Git;

/// <summary>
/// Applies the shared bounded retry policy to remote Git operations that can be safely repeated.
/// Publishing a Git ref is deliberately delegated once: a transport failure can leave the remote
/// outcome unknown, so replaying a push could overwrite a concurrent remote update.
/// </summary>
public sealed class RetryingGitRepositoryManager(IGitRepositoryManager inner, RetryPolicy retryPolicy) : IGitRepositoryManager
{
    public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) =>
        RetryAsync(token => inner.EnsureBareRepositoryAsync(repositoryId, cloneUrl, authentication, token), cancellationToken);

    public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) =>
        RetryAsync(token => inner.FetchAsync(repositoryId, authentication, token), cancellationToken);

    public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        inner.ResolveBranchCommitAsync(repositoryId, branchName, cancellationToken);

    public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        inner.TryResolveRemoteBranchCommitAsync(repositoryId, branchName, cancellationToken);

    public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) =>
        inner.IsAncestorAsync(repositoryId, ancestorCommit, descendantCommit, cancellationToken);

    public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) =>
        inner.CreateWorktreeAsync(repositoryId, worktreeId, worktreePath, branchName, baseCommit, cancellationToken);

    public ValueTask RenameWorktreeBranchAsync(string repositoryId, string worktreePath, string expectedCurrentBranch, string newBranchName, CancellationToken cancellationToken) =>
        inner.RenameWorktreeBranchAsync(repositoryId, worktreePath, expectedCurrentBranch, newBranchName, cancellationToken);

    public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken) =>
        inner.ResetWorktreeAsync(repositoryId, worktreePath, commit, cancellationToken);

    public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) =>
        inner.HasUncommittedChangesAsync(repositoryId, worktreePath, cancellationToken);

    public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) =>
        inner.GetHeadCommitAsync(repositoryId, worktreePath, cancellationToken);

    public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) =>
        RetryAsync(token => inner.UpdateSubmodulesAsync(repositoryId, worktreePath, authenticationResolver, token), cancellationToken);

    public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) =>
        inner.TryRebaseOntoAsync(repositoryId, worktreePath, ontoCommit, identity, cancellationToken);

    public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) =>
        inner.TryMergeAsync(repositoryId, worktreePath, commit, identity, cancellationToken);

    public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) =>
        inner.PushAsync(repositoryId, worktreePath, branchName, authentication, cancellationToken);

    public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) =>
        inner.RemoveWorktreeAsync(repositoryId, worktreeId, worktreePath, cancellationToken);

    public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        inner.RemoveLocalBranchAsync(repositoryId, branchName, cancellationToken);

    public bool WorktreeRequiresLfs(string worktreePath) => inner.WorktreeRequiresLfs(worktreePath);

    public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, Func<string, GitAuthentication?> submoduleAuthenticationResolver, CancellationToken cancellationToken) =>
        RetryAsync(token => inner.MaterializeLfsContentAsync(repositoryId, worktreePath, authentication, submoduleAuthenticationResolver, token), cancellationToken);

    public ValueTask PublishChangedSubmodulesAsync(
        string repositoryId,
        string worktreePath,
        string baseCommit,
        string branchName,
        GitAuthentication authentication,
        Func<string, GitAuthentication?> submoduleAuthenticationResolver,
        CancellationToken cancellationToken) =>
        RetryAsync(
            token => inner.PublishChangedSubmodulesAsync(
                repositoryId,
                worktreePath,
                baseCommit,
                branchName,
                authentication,
                submoduleAuthenticationResolver,
                token),
            cancellationToken);

    public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) =>
        RetryAsync(token => inner.UploadLfsObjectsAsync(repositoryId, worktreePath, branchName, authentication, token), cancellationToken);

    private async ValueTask RetryAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
    {
        var exception = await retryPolicy.ExecuteAsync(
            async token => await operation(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
