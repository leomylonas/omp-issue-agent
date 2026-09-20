using System.Diagnostics;
using IssueAgent.Git;
using Microsoft.Extensions.Logging;

namespace IssueAgent.Observability;

/// <summary>Wraps an <see cref="IGitRepositoryManager"/> with tracing, metrics, and structured
/// logging for every operation, without changing the wrapped implementation.</summary>
public sealed class ObservableGitRepositoryManager(IGitRepositoryManager inner, IssueAgentMetrics metrics, ILogger<ObservableGitRepositoryManager> logger) : IGitRepositoryManager
{
    public async ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) =>
        await RunAsync("ensure-bare-repository", repositoryId, () => inner.EnsureBareRepositoryAsync(repositoryId, cloneUrl, authentication, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) =>
        await RunAsync("fetch", repositoryId, () => inner.FetchAsync(repositoryId, authentication, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        await RunAsync("resolve-branch-commit", repositoryId, () => inner.ResolveBranchCommitAsync(repositoryId, branchName, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<string?> TryResolveRemoteBranchCommitAsync(
        string repositoryId,
        string branchName,
        CancellationToken cancellationToken) =>
        await RunAsync(
            "resolve-remote-branch-commit",
            repositoryId,
            () => inner.TryResolveRemoteBranchCommitAsync(repositoryId, branchName, cancellationToken).AsTask(),
            cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<bool> IsAncestorAsync(
        string repositoryId,
        string ancestorCommit,
        string descendantCommit,
        CancellationToken cancellationToken) =>
        await RunAsync(
            "is-ancestor",
            repositoryId,
            () => inner.IsAncestorAsync(repositoryId, ancestorCommit, descendantCommit, cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) =>
        await RunAsync("create-worktree", repositoryId, () => inner.CreateWorktreeAsync(repositoryId, worktreeId, worktreePath, branchName, baseCommit, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
    public async ValueTask RenameWorktreeBranchAsync(
        string repositoryId,
        string worktreePath,
        string expectedCurrentBranch,
        string newBranchName,
        CancellationToken cancellationToken) =>
        await RunAsync(
            "rename-worktree-branch",
            repositoryId,
            () => inner.RenameWorktreeBranchAsync(
                repositoryId,
                worktreePath,
                expectedCurrentBranch,
                newBranchName,
                cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);


    public async ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken) =>
        await RunAsync("reset-worktree", repositoryId, () => inner.ResetWorktreeAsync(repositoryId, worktreePath, commit, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) =>
        await RunAsync("has-uncommitted-changes", repositoryId, () => inner.HasUncommittedChangesAsync(repositoryId, worktreePath, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) =>
        await RunAsync("get-head-commit", repositoryId, () => inner.GetHeadCommitAsync(repositoryId, worktreePath, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) =>
        await RunAsync("update-submodules", repositoryId, () => inner.UpdateSubmodulesAsync(repositoryId, worktreePath, authenticationResolver, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) =>
        await RunAsync("rebase-onto", repositoryId, () => inner.TryRebaseOntoAsync(repositoryId, worktreePath, ontoCommit, identity, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) =>
        await RunAsync("merge", repositoryId, () => inner.TryMergeAsync(repositoryId, worktreePath, commit, identity, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) =>
        await RunAsync("push", repositoryId, () => inner.PushAsync(repositoryId, worktreePath, branchName, authentication, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) =>
        await RunAsync("remove-worktree", repositoryId, () => inner.RemoveWorktreeAsync(repositoryId, worktreeId, worktreePath, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        await RunAsync("remove-local-branch", repositoryId, () => inner.RemoveLocalBranchAsync(repositoryId, branchName, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public bool WorktreeRequiresLfs(string worktreePath) => inner.WorktreeRequiresLfs(worktreePath);

    public async ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, Func<string, GitAuthentication?> submoduleAuthenticationResolver, CancellationToken cancellationToken) =>
        await RunLfsAsync("materialize", repositoryId, () => inner.MaterializeLfsContentAsync(repositoryId, worktreePath, authentication, submoduleAuthenticationResolver, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask PublishChangedSubmodulesAsync(
        string repositoryId,
        string worktreePath,
        string baseCommit,
        string branchName,
        GitAuthentication authentication,
        Func<string, GitAuthentication?> submoduleAuthenticationResolver,
        CancellationToken cancellationToken) =>
        await RunAsync(
            "publish-changed-submodules",
            repositoryId,
            () => inner.PublishChangedSubmodulesAsync(
                repositoryId,
                worktreePath,
                baseCommit,
                branchName,
                authentication,
                submoduleAuthenticationResolver,
                cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) =>
        await RunLfsAsync("upload", repositoryId, () => inner.UploadLfsObjectsAsync(repositoryId, worktreePath, branchName, authentication, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    private async Task RunAsync(string operation, string? repositoryId, Func<Task> action, CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartGitOperation(operation, repositoryId ?? "unknown");
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, operation) };
        var stopwatch = Stopwatch.StartNew();
        metrics.GitOperations.Add(1, tags);
        try
        {
            await action().ConfigureAwait(false);
            GitLogMessages.GitOperationSucceeded(logger, operation, repositoryId, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown/agent-cancel is normal operation (specification §27), not a Git
            // failure; must never inflate issueagent_git_errors_total.
            throw;
        }
        catch (Exception ex)
        {
            metrics.GitErrors.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error);
            GitLogMessages.GitOperationFailed(logger, ex.GetType().Name, operation, repositoryId);
            throw;
        }
        finally
        {
            metrics.GitDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }

    private async Task<T> RunAsync<T>(string operation, string? repositoryId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        T result = default!;
        Func<Task> nonGenericAction = async () => result = await action().ConfigureAwait(false);
        await RunAsync(operation, repositoryId, nonGenericAction, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task RunLfsAsync(string operation, string? repositoryId, Func<Task> action, CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartLfsOperation(operation, repositoryId ?? "unknown");
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, operation) };
        var stopwatch = Stopwatch.StartNew();
        metrics.LfsOperations.Add(1, tags);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.LfsErrors.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error);
            GitLogMessages.LfsOperationFailed(logger, ex.GetType().Name, operation);
            throw;
        }
        finally
        {
            metrics.LfsDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }
}
