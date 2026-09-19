namespace IssueAgent.Git;

/// <summary>
/// Provider-neutral Git workspace operations. Implementations own bare repository storage,
/// retained worktrees, branch/commit resolution, and publication. No provider, LibGit2Sharp, or
/// transport types appear in this contract.
/// </summary>
public interface IGitRepositoryManager
{
    /// <summary>Ensures the bare canonical repository for <paramref name="repositoryId"/> exists at
    /// its configured storage path, cloning lazily on first use. Idempotent.</summary>
    ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken);

    /// <summary>Fetches all refs for the bare canonical repository.</summary>
    ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken);

    /// <summary>Resolves <paramref name="branchName"/> to its exact current commit SHA on the bare
    /// canonical repository. Throws <see cref="GitReferenceNotFoundException"/> if absent.</summary>
    ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken);

    /// <summary>Returns the fetched remote-tracking head for <paramref name="branchName"/>, or
    /// <see langword="null"/> when the remote branch does not exist. Never falls back to a local branch.</summary>
    ValueTask<string?> TryResolveRemoteBranchCommitAsync(
        string repositoryId,
        string branchName,
        CancellationToken cancellationToken);

    /// <summary>Returns whether <paramref name="ancestorCommit"/> is an ancestor of
    /// <paramref name="descendantCommit"/> in the fetched canonical repository history.</summary>
    ValueTask<bool> IsAncestorAsync(
        string repositoryId,
        string ancestorCommit,
        string descendantCommit,
        CancellationToken cancellationToken);

    /// <summary>Creates a retained worktree at <paramref name="worktreePath"/> checking out
    /// <paramref name="branchName"/>, creating the branch from <paramref name="baseCommit"/> if it
    /// does not already exist locally. Idempotent when the worktree already exists at that commit.</summary>
    ValueTask CreateWorktreeAsync(
        string repositoryId,
        string worktreeId,
        string worktreePath,
        string branchName,
        string baseCommit,
        CancellationToken cancellationToken);

    /// <summary>Hard-resets the worktree's working directory and index to <paramref name="commit"/>,
    /// discarding uncommitted changes. Used to restore a clean planned base before implementation.</summary>
    ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken);

    /// <summary>True when the worktree has uncommitted changes (tracked or untracked).</summary>
    ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken);

    /// <summary>Returns the worktree's current HEAD commit SHA.</summary>
    ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken);

    /// <summary>Initializes and updates the worktree's direct (non-recursive) submodules using the supplied
    /// per-host authentication resolver. A submodule host outside <paramref name="authenticationResolver"/>'s
    /// known hosts is updated anonymously; failure when authentication is required is reported via
    /// <see cref="SubmoduleAuthenticationRequiredException"/>.</summary>
    ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken);

    /// <summary>Rebases the worktree's current branch onto <paramref name="ontoCommit"/>. Returns
    /// <see langword="false"/> and aborts cleanly if conflicts occur.</summary>
    ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken);

    /// <summary>Merges <paramref name="commit"/> into the worktree's current branch. Returns
    /// <see langword="false"/> and leaves conflict markers staged if conflicts occur, rather than
    /// aborting, so OMP can resolve them.</summary>
    ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken);

    /// <summary>Pushes <paramref name="branchName"/> from the worktree to its configured remote.
    /// Never force-pushes.</summary>
    ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken);

    /// <summary>Removes the worktree and its administrative metadata. Does not touch the bare
    /// canonical repository or the remote.</summary>
    ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken);

    /// <summary>Deletes a local branch from the bare canonical repository. Never touches the remote.</summary>
    ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken);

    /// <summary>True when the worktree's committed <c>.gitattributes</c> declares an LFS filter.</summary>
    bool WorktreeRequiresLfs(string worktreePath);

    /// <summary>Replaces LFS pointer files in the worktree with real content. Throws
    /// <see cref="GitLfsUnavailableException"/> if <c>git-lfs</c> is not available.</summary>
    ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken);

    /// <summary>Uploads LFS objects referenced by <paramref name="branchName"/> that the remote does
    /// not already have. Must be called, and must succeed, before <see cref="PushAsync"/> publishes
    /// the corresponding ref.</summary>
    ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken);
}

public sealed record GitIdentity(string Name, string Email);

public sealed class GitReferenceNotFoundException(string message) : Exception(message);

public sealed class SubmoduleAuthenticationRequiredException(string message) : Exception(message);

public sealed class GitHooksPresentException(string message) : Exception(message);
