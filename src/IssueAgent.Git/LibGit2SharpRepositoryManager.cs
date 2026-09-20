using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using System.Runtime.Versioning;

namespace IssueAgent.Git;

/// <summary>
/// LibGit2Sharp-backed <see cref="IGitRepositoryManager"/>.
///
/// The LibGit2Sharp 0.32 native binaries ship without libssh2, so this library exposes no SSH
/// transport credential type at all. HTTPS-family modes (<c>provider-token</c>, <c>token</c>,
/// <c>anonymous</c>) use LibGit2Sharp's native transport. <see cref="GitAuthenticationMode.Ssh"/>
/// therefore shells out to the <c>git</c>/<c>ssh</c> executables via <see cref="GitSshTransport"/>
/// for the remote-touching operations (clone, fetch, push); all local operations (worktrees,
/// branch resolution, reset, merge, rebase) still use LibGit2Sharp regardless of transport.
///
/// Every bare repository and worktree configures a host-owned, empty <c>core.hooksPath</c> so no
/// Git hook can ever execute. Remote-touching git CLI calls run with <c>GIT_CONFIG_NOSYSTEM=1</c>
/// and an isolated <c>HOME</c>/<c>XDG_CONFIG_HOME</c> so ambient host/global Git config is never inherited.
/// </summary>
public sealed class LibGit2SharpRepositoryManager(string reposRootPath) : IGitRepositoryManager
{
    private static readonly string[] mutableBareRepositoryDirectories = ["objects", "refs", "worktrees"];
    private readonly string normalizedReposRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reposRootPath));



    public async ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        var path = BareRepositoryPath(repositoryId);
        if (Repository.IsValid(path))
        {
            DisableHooks(path);
            HardenBareRepositoryAuthority(path);
            return;
        }

        if (Directory.Exists(path))
        {
            // A transport failure can leave a partial clone directory. The canonical path is owned
            // by this manager and is not a repository, so discard it before a safe retry.
            DeleteDirectoryRobust(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (authentication.Mode == GitAuthenticationMode.Ssh)
        {
            await GitSshTransport.CloneBareAsync(cloneUrl, path, authentication, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new CloneOptions
            {
                IsBare = true,
                Checkout = false,
                FetchOptions =
                {
                    CredentialsProvider = CredentialsHandlerFor(authentication, TryGetTransportAuthority(cloneUrl)),
                    CertificateCheck = CertificateCheckHandlerFor(authentication.TlsTrust),
                    OnTransferProgress = _ => !cancellationToken.IsCancellationRequested,
                },
            };
            Repository.Clone(cloneUrl, path, options);
        }

        DisableHooks(path);
        HardenBareRepositoryAuthority(path);
    }

    public async ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        var path = BareRepositoryPath(repositoryId);
        if (authentication.Mode == GitAuthenticationMode.Ssh)
        {
            await GitSshTransport.FetchAsync(path, authentication, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repo = new Repository(path);
            var remote = repo.Network.Remotes["origin"];
            var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
            Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
            {
                CredentialsProvider = CredentialsHandlerFor(authentication, TryGetTransportAuthority(remote.Url)),
                CertificateCheck = CertificateCheckHandlerFor(authentication.TlsTrust),
                OnTransferProgress = _ => !cancellationToken.IsCancellationRequested,
            }, logMessage: null);
        }

        // Fetch creates objects and refs below the shared bare database. Reapply the boundary
        // after every fetch so newly created entries are usable by OMP without exposing authority.
        HardenBareRepositoryAuthority(path);
    }

    public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken)
    {
        using var repo = new Repository(BareRepositoryPath(repositoryId));
        var branch = repo.Branches[$"origin/{branchName}"] ?? repo.Branches[branchName];
        if (branch is null)
        {
            throw new GitReferenceNotFoundException($"Branch '{branchName}' was not found in repository '{repositoryId}'.");
        }

        return ValueTask.FromResult(branch.Tip.Sha);
    }

    public ValueTask<string?> TryResolveRemoteBranchCommitAsync(
        string repositoryId,
        string branchName,
        CancellationToken cancellationToken)
    {
        using var repo = new Repository(BareRepositoryPath(repositoryId));
        return ValueTask.FromResult(repo.Branches[$"origin/{branchName}"]?.Tip.Sha);
    }

    public ValueTask<bool> IsAncestorAsync(
        string repositoryId,
        string ancestorCommit,
        string descendantCommit,
        CancellationToken cancellationToken)
    {
        using var repo = new Repository(BareRepositoryPath(repositoryId));
        var ancestor = repo.Lookup<Commit>(ancestorCommit);
        var descendant = repo.Lookup<Commit>(descendantCommit);
        if (ancestor is null || descendant is null)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(
            repo.ObjectDatabase.FindMergeBase(ancestor, descendant)?.Sha == ancestor.Sha);
    }

    public ValueTask CreateWorktreeAsync(
        string repositoryId,
        string worktreeId,
        string worktreePath,
        string branchName,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        var barePath = BareRepositoryPath(repositoryId);
        var worktreesMetadataPath = Path.Combine(barePath, "worktrees");
        if (Directory.Exists(worktreesMetadataPath))
        {
            foreach (var metadataPath in Directory.GetDirectories(worktreesMetadataPath))
            {
                var gitDirectoryPath = Path.Combine(metadataPath, "gitdir");
                if (File.Exists(gitDirectoryPath))
                {
                    var checkoutGitPath = File.ReadAllText(gitDirectoryPath).Trim();
                    if (!File.Exists(checkoutGitPath) && !Directory.Exists(checkoutGitPath))
                    {
                        Directory.Delete(metadataPath, recursive: true);
                    }
                }
            }
        }
        using var repo = new Repository(barePath);
        if (!Directory.Exists(worktreePath) &&
            !string.Equals(worktreeId, branchName, StringComparison.Ordinal) &&
            repo.Branches[worktreeId] is { } staleWorktreeBranch)
        {
            repo.Branches.Remove(staleWorktreeBranch);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // LibGit2Sharp can retain a registration after an interrupted directory removal.
        // Recreating the same durable workflow must prune that stale registration instead of
        // failing with "worktree already exists"; a live registration at the requested path is
        // already the idempotent success case.
        Worktree? existingWorktree = null;
        foreach (var candidate in repo.Worktrees.ToList())
        {
            try
            {
                if (string.Equals(candidate.Name, worktreeId, StringComparison.Ordinal))
                {
                    existingWorktree = candidate;
                    break;
                }
            }
            catch (NullReferenceException)
            {
                // A registration whose checkout directory disappeared can throw while LibGit2Sharp
                // materializes its name. It is stale by definition and safe to prune.
                repo.Worktrees.Prune(candidate, ifLocked: true);
            }
        }

        if (existingWorktree is not null)
        {
            if (Directory.Exists(worktreePath))
            {
                DisableHooks(worktreePath);
                ConfigureLfsFilters(worktreePath);
                if (OperatingSystem.IsLinux())
                {
                    HardenBareRepositoryAuthority(barePath);
                    MakeWorktreeWritableByOmp(worktreePath);
                    MakeLinkedWorktreeMetadataWritableByOmp(barePath, worktreeId);
                }
                return ValueTask.CompletedTask;
            }

            repo.Worktrees.Prune(existingWorktree, ifLocked: true);
        }

        var branch = repo.Branches[branchName];
        if (branch is null)
        {
            var commit = (Commit)repo.Lookup(baseCommit, ObjectType.Commit);
            branch = repo.Branches.Add(branchName, commit);
        }

        repo.Worktrees.Add(branch.CanonicalName, worktreeId, worktreePath, isLocked: false);
        DisableHooks(worktreePath);
        ConfigureLfsFilters(worktreePath);
        if (OperatingSystem.IsLinux())
        {
            HardenBareRepositoryAuthority(barePath);
            MakeWorktreeWritableByOmp(worktreePath);
            MakeLinkedWorktreeMetadataWritableByOmp(barePath, worktreeId);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameWorktreeBranchAsync(
        string repositoryId,
        string worktreePath,
        string expectedCurrentBranch,
        string newBranchName,
        CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        var currentBranch = repo.Head.FriendlyName;
        if (string.Equals(currentBranch, newBranchName, StringComparison.Ordinal))
        {
            // The branch rename completed before the durable workflow checkpoint could be
            // published. Treat the retry as reconciled rather than trying to rename it again.
            return ValueTask.CompletedTask;
        }

        if (!string.Equals(currentBranch, expectedCurrentBranch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Retained worktree '{worktreePath}' is checked out on '{currentBranch}', not expected branch '{expectedCurrentBranch}' or retry branch '{newBranchName}'.");
        }

        if (string.Equals(expectedCurrentBranch, newBranchName, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        if (repo.Branches[newBranchName] is not null)
        {
            throw new InvalidOperationException($"Cannot rename branch '{expectedCurrentBranch}' to existing branch '{newBranchName}'.");
        }

        repo.Branches.Rename(repo.Head, newBranchName);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        var target = (Commit)repo.Lookup(commit, ObjectType.Commit);
        repo.Reset(ResetMode.Hard, target);
        repo.RemoveUntrackedFiles();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        var status = repo.RetrieveStatus(new StatusOptions());
        return ValueTask.FromResult(status.IsDirty);
    }

    public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        return ValueTask.FromResult(repo.Head.Tip.Sha);
    }

    public async ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationResolver);
        cancellationToken.ThrowIfCancellationRequested();
        var rootPath = Path.GetFullPath(worktreePath);
        await UpdateSubmodulesRecursivelyAsync(rootPath, rootPath, authenticationResolver, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateSubmodulesRecursivelyAsync(
        string repositoryPath,
        string rootPath,
        Func<string, GitAuthentication?> authenticationResolver,
        ISet<string> visitedRepositoryPaths,
        CancellationToken cancellationToken)
    {
        using var repo = new Repository(repositoryPath);
        var repositoryIdentity = Path.GetFullPath(repo.Info.Path);
        if (!visitedRepositoryPaths.Add(repositoryIdentity))
        {
            return;
        }

        var parentRemoteUrl = repo.Network.Remotes["origin"]?.Url;
        foreach (var submodule in repo.Submodules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var submodulePath = GetSafeSubmodulePath(rootPath, repositoryPath, submodule.Path);
            var resolvedSubmoduleUrl = parentRemoteUrl is null
                ? submodule.Url
                : ResolveSubmoduleUrl(submodule.Url, parentRemoteUrl);
            var transportAuthority = TryGetTransportAuthority(resolvedSubmoduleUrl);
            var authentication = transportAuthority is null
                ? null
                : authenticationResolver(resolvedSubmoduleUrl);
            var effectiveAuthentication = authentication ?? GitAuthentication.Anonymous(TlsTrust.System);

            if (effectiveAuthentication.Mode == GitAuthenticationMode.Ssh)
            {
                await GitSshTransport.UpdateSubmoduleAsync(
                    repositoryPath,
                    submodule.Path,
                    GetSshTransportRemoteUrl(resolvedSubmoduleUrl, parentRemoteUrl),
                    effectiveAuthentication,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions
                    {
                        Init = true,
                        FetchOptions =
                        {
                            CredentialsProvider = CredentialsHandlerFor(effectiveAuthentication, transportAuthority),
                            CertificateCheck = CertificateCheckHandlerFor(effectiveAuthentication.TlsTrust),
                            OnTransferProgress = _ => !cancellationToken.IsCancellationRequested,
                        },
                    });
                }
                catch (LibGit2SharpException) when (authentication is null)
                {
                    throw new SubmoduleAuthenticationRequiredException(
                        $"Submodule '{submodule.Name}' at '{submodule.Url}' requires authentication that is not configured for its host.");
                }
            }

            GetSafeSubmodulePath(rootPath, repositoryPath, submodule.Path);
            DisableHooks(submodulePath);
            await UpdateSubmodulesRecursivelyAsync(submodulePath, rootPath, authenticationResolver, visitedRepositoryPaths, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetSshTransportRemoteUrl(string submoduleUrl, string? parentRemoteUrl)
    {
        if (IsSshRemoteUrl(submoduleUrl))
        {
            return submoduleUrl;
        }

        if (TryGetHost(submoduleUrl) is null && parentRemoteUrl is not null && IsSshRemoteUrl(parentRemoteUrl))
        {
            // Relative URLs are resolved before this method is called. Retain this fallback for
            // callers that provide a local parent remote, where no SSH target can be derived.
            return parentRemoteUrl;
        }

        throw new InvalidOperationException(
            $"Submodule '{submoduleUrl}' cannot use SSH authentication because its SSH endpoint is ambiguous.");
    }

    private static bool IsSshRemoteUrl(string remoteUrl)
    {
        try
        {
            _ = GitSshTransport.ParseSshHostAndPort(remoteUrl);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetSafeSubmodulePath(string rootPath, string repositoryPath, string submodulePath)
    {
        var normalizedRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedRepositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var path = Path.GetFullPath(Path.Combine(normalizedRepositoryPath, submodulePath));
        var relativeToRoot = Path.GetRelativePath(normalizedRootPath, path);
        if (Path.IsPathRooted(relativeToRoot) ||
            string.Equals(relativeToRoot, "..", StringComparison.Ordinal) ||
            relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Submodule path '{submodulePath}' escapes worktree '{rootPath}'.");
        }

        var relativeToRepository = Path.GetRelativePath(normalizedRepositoryPath, path);
        var currentPath = normalizedRepositoryPath;
        foreach (var segment in relativeToRepository.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Submodule path '{submodulePath}' traverses a symbolic link.");
                }
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }

        return path;
    }

    public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        var currentBranch = repo.Branches[repo.Head.FriendlyName];
        var ontoCommitObj = (Commit)repo.Lookup(ontoCommit, ObjectType.Commit);
        var mergeBase = repo.ObjectDatabase.FindMergeBase(currentBranch.Tip, ontoCommitObj);

        var upstreamBranchName = $"issueagent-rebase-upstream-{Guid.NewGuid():N}";
        var ontoBranchName = $"issueagent-rebase-onto-{Guid.NewGuid():N}";
        var upstreamBranch = repo.Branches.Add(upstreamBranchName, mergeBase);
        var ontoBranch = repo.Branches.Add(ontoBranchName, ontoCommitObj);

        var result = repo.Rebase.Start(currentBranch, upstreamBranch, ontoBranch, new Identity(identity.Name, identity.Email), new RebaseOptions());
        if (result.Status == RebaseStatus.Conflicts)
        {
            // LibGit2Sharp's rebase state cannot be continued by the Git CLI that OMP uses. Abort
            // that in-memory rebase, then reproduce the same integration as a merge so the conflict
            // index is durable and an ordinary `git commit` publishes a branch containing the target.
            repo.Rebase.Abort();
            repo.Branches.Remove(upstreamBranch);
            repo.Branches.Remove(ontoBranch);
            return ValueTask.FromResult(
                repo.Merge(ontoCommitObj, new Signature(identity.Name, identity.Email, DateTimeOffset.UtcNow), new MergeOptions { CommitOnSuccess = true, FailOnConflict = false }).Status != MergeStatus.Conflicts);
        }

        repo.Branches.Remove(upstreamBranch);
        repo.Branches.Remove(ontoBranch);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        var target = (Commit)repo.Lookup(commit, ObjectType.Commit);
        var merger = new Signature(identity.Name, identity.Email, DateTimeOffset.UtcNow);

        var result = repo.Merge(target, merger, new MergeOptions { CommitOnSuccess = true, FailOnConflict = false });
        return ValueTask.FromResult(result.Status != MergeStatus.Conflicts);
    }

    public async ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        // Do not trust worktreePath/.git here: OMP can rewrite it. The registered linked-worktree
        // metadata is owned by the hardened bare repository and identifies the real index.
        var bareRepositoryPath = BareRepositoryPath(repositoryId);
        if (HasTrustedUncommittedChanges(bareRepositoryPath, worktreePath))
        {
            throw new InvalidOperationException("Cannot publish a worktree with uncommitted changes.");
        }

        // OMP can edit a retained worktree, including its .git file. Resolve both the ref and
        // remote from the canonical bare repository, which is never made group-writable, so a
        // worktree cannot redirect a credential-bearing push.
        if (authentication.Mode == GitAuthenticationMode.Ssh)
        {
            await GitSshTransport.PushAsync(bareRepositoryPath, branchName, authentication, cancellationToken).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var repo = new Repository(bareRepositoryPath);
        if (repo.Branches[branchName] is null)
        {
            throw new GitReferenceNotFoundException($"Local branch '{branchName}' was not found.");
        }

        var remote = repo.Network.Remotes["origin"];
        repo.Network.Push(remote, $"refs/heads/{branchName}:refs/heads/{branchName}", new PushOptions
        {
            CredentialsProvider = CredentialsHandlerFor(authentication, TryGetTransportAuthority(remote.Url)),
            CertificateCheck = CertificateCheckHandlerFor(authentication.TlsTrust),
            OnPushTransferProgress = (_, _, _) => !cancellationToken.IsCancellationRequested,
        });
    }

    public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken)
    {
        using (var repo = new Repository(BareRepositoryPath(repositoryId)))
        {
            if (repo.Worktrees.FirstOrDefault(w => w.Name == worktreeId) is { } worktree)
            {
                repo.Worktrees.Prune(worktree, ifLocked: true);
            }
        }

        if (Directory.Exists(worktreePath))
        {
            DeleteDirectoryRobust(worktreePath);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken)
    {
        using var repo = new Repository(BareRepositoryPath(repositoryId));
        if (repo.Branches[branchName] is { } branch)
        {
            repo.Branches.Remove(branch);
        }
        return ValueTask.CompletedTask;
    }

    public bool WorktreeRequiresLfs(string worktreePath) => GitLfsRunner.RepositoryRequiresLfs(worktreePath);

    public async ValueTask MaterializeLfsContentAsync(
        string repositoryId,
        string worktreePath,
        GitAuthentication authentication,
        Func<string, GitAuthentication?> submoduleAuthenticationResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submoduleAuthenticationResolver);
        await MaterializeLfsContentRecursivelyAsync(
            worktreePath,
            Path.GetFullPath(worktreePath),
            GetCanonicalOriginUrl(repositoryId),
            authentication,
            submoduleAuthenticationResolver,
            new HashSet<string>(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task MaterializeLfsContentRecursivelyAsync(
        string repositoryPath,
        string rootPath,
        string rootCanonicalRemoteUrl,
        GitAuthentication rootAuthentication,
        Func<string, GitAuthentication?> submoduleAuthenticationResolver,
        ISet<string> visitedRepositoryPaths,
        CancellationToken cancellationToken)
    {
        using var repo = new Repository(repositoryPath);
        var repositoryIdentity = Path.GetFullPath(repo.Info.Path);
        if (!visitedRepositoryPaths.Add(repositoryIdentity))
        {
            return;
        }

        var isRootRepository = string.Equals(Path.GetFullPath(repositoryPath), rootPath, StringComparison.Ordinal);
        var canonicalRemoteUrl = isRootRepository
            ? rootCanonicalRemoteUrl
            : repo.Network.Remotes["origin"]?.Url
                ?? throw new InvalidOperationException($"Repository '{repositoryPath}' has no origin remote for LFS materialization.");
        var authentication = isRootRepository
            ? rootAuthentication
            : GitUrlHost.TryGetTransportAuthority(canonicalRemoteUrl) is not null
                ? submoduleAuthenticationResolver(canonicalRemoteUrl) ?? GitAuthentication.Anonymous(TlsTrust.System)
                : GitAuthentication.Anonymous(TlsTrust.System);

        if (GitLfsRunner.RepositoryRequiresLfs(repositoryPath))
        {
            await GitLfsRunner.MaterializeContentAsync(
                repositoryPath,
                canonicalRemoteUrl,
                authentication,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var submodule in repo.Submodules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var submodulePath = GetSafeSubmodulePath(rootPath, repositoryPath, submodule.Path);
            if (Directory.Exists(submodulePath))
            {
                await MaterializeLfsContentRecursivelyAsync(
                    submodulePath,
                    rootPath,
                    rootCanonicalRemoteUrl,
                    rootAuthentication,
                    submoduleAuthenticationResolver,
                    visitedRepositoryPaths,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask PublishChangedSubmodulesAsync(
        string repositoryId,
        string worktreePath,
        string baseCommit,
        string branchName,
        GitAuthentication authentication,
        Func<string, GitAuthentication?> submoduleAuthenticationResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submoduleAuthenticationResolver);
        using var repository = new Repository(worktreePath);
        var baseTree = repository.Lookup<Commit>(baseCommit)?.Tree
            ?? throw new GitReferenceNotFoundException($"Base commit '{baseCommit}' was not found.");
        var head = repository.Head.Tip
            ?? throw new GitReferenceNotFoundException("Worktree has no HEAD commit.");

        foreach (var change in repository.Diff.Compare<TreeChanges>(baseTree, head.Tree)
                     .Where(change => change.Status != ChangeKind.Deleted &&
                                      head.Tree[change.Path]?.TargetType == TreeEntryTargetType.GitLink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var submodulePath = GetSafeSubmodulePath(Path.GetFullPath(worktreePath), Path.GetFullPath(worktreePath), change.Path);
            if (!Directory.Exists(submodulePath))
            {
                throw new InvalidOperationException(
                    $"Changed submodule '{change.Path}' is not initialized and cannot be published safely.");
            }

            var expectedCommit = head.Tree[change.Path]!.Target.Id.Sha;
            var previousCommit = baseTree[change.Path]?.TargetType == TreeEntryTargetType.GitLink
                ? baseTree[change.Path]!.Target.Id.Sha
                : null;
            var authoritativeRemoteUrl = GetCommittedSubmoduleRemoteUrl(
                repository,
                head,
                change.Path,
                GetCanonicalOriginUrl(repositoryId));
            await PublishSubmoduleRecursivelyAsync(
                submodulePath,
                expectedCommit,
                previousCommit,
                branchName,
                authoritativeRemoteUrl,
                submoduleAuthenticationResolver,
                new HashSet<string>(StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PublishSubmoduleRecursivelyAsync(
        string repositoryPath,
        string expectedCommit,
        string? previousCommit,
        string branchName,
        string authoritativeRemoteUrl,
        Func<string, GitAuthentication?> authenticationResolver,
        ISet<string> visitedRepositoryPaths,
        CancellationToken cancellationToken)
    {
        using var repository = new Repository(repositoryPath);
        var repositoryIdentity = Path.GetFullPath(repository.Info.Path);
        if (!visitedRepositoryPaths.Add(repositoryIdentity))
        {
            return;
        }

        var head = repository.Head.Tip
            ?? throw new InvalidOperationException($"Changed submodule '{repositoryPath}' has no HEAD commit.");
        if (!string.Equals(head.Sha, expectedCommit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Changed submodule '{repositoryPath}' is checked out at '{head.Sha}', not its committed gitlink '{expectedCommit}'.");
        }

        var remote = repository.Network.Remotes["origin"]
            ?? throw new InvalidOperationException($"Changed submodule '{repositoryPath}' has no origin remote.");
        if (!string.Equals(remote.Url, authoritativeRemoteUrl, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(remote.PushUrl) &&
            !string.Equals(remote.PushUrl, authoritativeRemoteUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Changed submodule '{repositoryPath}' origin does not match its committed .gitmodules URL.");
        }

        var transportAuthority = GitUrlHost.TryGetTransportAuthority(authoritativeRemoteUrl);
        var authentication = transportAuthority is null
            ? GitAuthentication.Anonymous(TlsTrust.System)
            : authenticationResolver(authoritativeRemoteUrl) ?? GitAuthentication.Anonymous(TlsTrust.System);
        var previousTree = previousCommit is null
            ? null
            : repository.Lookup<Commit>(previousCommit)?.Tree
                ?? throw new InvalidOperationException(
                    $"Previous submodule commit '{previousCommit}' for '{repositoryPath}' is unavailable for safe publication.");

        foreach (var submodule in repository.Submodules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gitlink = head.Tree[submodule.Path];
            if (gitlink?.TargetType != TreeEntryTargetType.GitLink)
            {
                throw new InvalidOperationException(
                    $"Nested submodule '{submodule.Path}' of changed submodule '{repositoryPath}' has no committed gitlink.");
            }

            var previousGitlink = previousTree?[submodule.Path];
            if (previousGitlink?.TargetType == TreeEntryTargetType.GitLink &&
                string.Equals(previousGitlink.Target.Id.Sha, gitlink.Target.Id.Sha, StringComparison.Ordinal))
            {
                continue;
            }

            var submodulePath = GetSafeSubmodulePath(repositoryPath, repositoryPath, submodule.Path);
            if (!Directory.Exists(submodulePath))
            {
                throw new InvalidOperationException(
                    $"Nested submodule '{submodule.Path}' of changed submodule '{repositoryPath}' is not initialized and cannot be published safely.");
            }

            var nestedAuthoritativeRemoteUrl = GetCommittedSubmoduleRemoteUrl(
                repository,
                head,
                submodule.Path,
                authoritativeRemoteUrl);
            await PublishSubmoduleRecursivelyAsync(
                submodulePath,
                gitlink.Target.Id.Sha,
                previousGitlink?.TargetType == TreeEntryTargetType.GitLink
                    ? previousGitlink.Target.Id.Sha
                    : null,
                branchName,
                nestedAuthoritativeRemoteUrl,
                authenticationResolver,
                visitedRepositoryPaths,
                cancellationToken).ConfigureAwait(false);
        }

        if (GitLfsRunner.RepositoryRequiresLfs(repositoryPath))
        {
            await GitLfsRunner.UploadObjectsAsync(repositoryPath, authoritativeRemoteUrl, head.Sha, authentication, cancellationToken)
                .ConfigureAwait(false);
        }

        if (authentication.Mode == GitAuthenticationMode.Ssh)
        {
            await GitSshTransport.PushCommitAsync(repositoryPath, authoritativeRemoteUrl, head.Sha, branchName, authentication, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        repository.Network.Push(remote, $"{head.Sha}:refs/heads/{branchName}", new PushOptions
        {
            CredentialsProvider = CredentialsHandlerFor(authentication, transportAuthority),
            CertificateCheck = CertificateCheckHandlerFor(authentication.TlsTrust),
            OnPushTransferProgress = (_, _, _) => !cancellationToken.IsCancellationRequested,
        });
    }

    public async ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        await GitLfsRunner.UploadObjectsAsync(
            worktreePath,
            GetCanonicalOriginUrl(repositoryId),
            branchName,
            authentication,
            cancellationToken).ConfigureAwait(false);
    }

    private static string GetCommittedSubmoduleRemoteUrl(
        Repository repository,
        Commit commit,
        string submodulePath,
        string parentAuthoritativeRemoteUrl)
    {
        var gitmodules = commit.Tree[".gitmodules"]?.Target as Blob
            ?? throw new InvalidOperationException(
                $"Changed submodule '{submodulePath}' has no committed .gitmodules entry.");
        using var reader = new StreamReader(gitmodules.GetContentStream());
        string? sectionPath = null;
        string? sectionUrl = null;

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[submodule ", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(']'))
            {
                if (string.Equals(sectionPath, submodulePath, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(sectionUrl))
                {
                    return ResolveSubmoduleUrl(sectionUrl, parentAuthoritativeRemoteUrl);
                }

                sectionPath = null;
                sectionUrl = null;
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                sectionPath = value;
            }
            else if (key.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                sectionUrl = value;
            }
        }

        if (string.Equals(sectionPath, submodulePath, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(sectionUrl))
        {
            return ResolveSubmoduleUrl(sectionUrl, parentAuthoritativeRemoteUrl);
        }

        throw new InvalidOperationException(
            $"Changed submodule '{submodulePath}' has no URL in committed .gitmodules.");
    }

    private static string ResolveSubmoduleUrl(string submoduleUrl, string parentRemoteUrl)
    {
        if (Uri.TryCreate(submoduleUrl, UriKind.Absolute, out _) || TryGetHost(submoduleUrl) is not null ||
            Path.IsPathRooted(submoduleUrl))
        {
            return submoduleUrl;
        }

        if (Uri.TryCreate(parentRemoteUrl, UriKind.Absolute, out var parentUri))
        {
            return new Uri(new Uri(parentUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), submoduleUrl).ToString();
        }

        if (TryNormalizeScpLikeSshRemote(parentRemoteUrl, out var scpParentUri))
        {
            var resolved = new Uri(new Uri(scpParentUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), submoduleUrl);
            var authority = parentRemoteUrl[..parentRemoteUrl.IndexOf(':')];
            return $"{authority}:{resolved.AbsolutePath.TrimStart('/')}";
        }

        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentRemoteUrl) ?? ".", submoduleUrl));
    }

    private static bool HasTrustedUncommittedChanges(string bareRepositoryPath, string worktreePath)
    {
        var expectedGitFile = Path.GetFullPath(Path.Combine(worktreePath, ".git"));
        var worktreesPath = Path.Combine(bareRepositoryPath, "worktrees");
        foreach (var metadataPath in Directory.GetDirectories(worktreesPath))
        {
            var gitDirectoryPath = Path.Combine(metadataPath, "gitdir");
            if (!File.Exists(gitDirectoryPath) ||
                !string.Equals(Path.GetFullPath(File.ReadAllText(gitDirectoryPath).Trim()), expectedGitFile, StringComparison.Ordinal))
            {
                continue;
            }

            using var worktreeRepository = new Repository(metadataPath);
            return worktreeRepository.RetrieveStatus(new StatusOptions()).IsDirty;
        }

        throw new InvalidOperationException("Cannot publish an unregistered worktree.");
    }

    private string GetCanonicalOriginUrl(string repositoryId)
    {
        using var repository = new Repository(BareRepositoryPath(repositoryId));
        return repository.Network.Remotes["origin"].Url;
    }

    private string BareRepositoryPath(string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) ||
            Path.IsPathRooted(repositoryId) ||
            repositoryId.StartsWith('\\') ||
            repositoryId.Split(['/', '\\'], StringSplitOptions.None).Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Repository IDs must be non-empty, relative, and contain no traversal segments.", nameof(repositoryId));
        }

        var path = Path.GetFullPath(Path.Combine(normalizedReposRootPath, repositoryId));
        var relativePath = Path.GetRelativePath(normalizedReposRootPath, path);
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, ".", StringComparison.Ordinal) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Repository ID resolves outside the repository cache root.", nameof(repositoryId));
        }

        return path;
    }

    private static void DisableHooks(string repositoryOrWorktreePath)
    {
        using var repo = new Repository(repositoryOrWorktreePath);
        var gitDirectoryPath = Path.GetFullPath(repo.Info.Path);
        var commonDirectoryMarkerPath = Path.Combine(gitDirectoryPath, "commondir");
        var commonGitDirectoryPath = File.Exists(commonDirectoryMarkerPath)
            ? Path.GetFullPath(File.ReadAllText(commonDirectoryMarkerPath).Trim(), gitDirectoryPath)
            : gitDirectoryPath;
        var hooksDir = Path.Combine(commonGitDirectoryPath, "issueagent-disabled-hooks");
        Directory.CreateDirectory(hooksDir);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                hooksDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        repo.Config.Set("core.hooksPath", hooksDir, ConfigurationLevel.Local);
    }

    /// <summary>Configures LFS smudge/clean/process filters locally, equivalent to what
    /// <c>git lfs install --local</c> would do, without ever running that command or any hook.</summary>
    private static void ConfigureLfsFilters(string worktreePath)
    {
        using var repo = new Repository(worktreePath);
        repo.Config.Set("filter.lfs.clean", "git-lfs clean -- %f", ConfigurationLevel.Local);
        repo.Config.Set("filter.lfs.smudge", "git-lfs smudge -- %f", ConfigurationLevel.Local);
        repo.Config.Set("filter.lfs.process", "git-lfs filter-process", ConfigurationLevel.Local);
        repo.Config.Set("filter.lfs.required", true, ConfigurationLevel.Local);
    }



    private void HardenBareRepositoryAuthority(string bareRepositoryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            bareRepositoryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        MakeAncestorDirectoriesTraversableByOmp(bareRepositoryPath, Path.GetDirectoryName(normalizedReposRootPath) ?? normalizedReposRootPath);
        var configPath = Path.Combine(bareRepositoryPath, "config");
        if (File.Exists(configPath))
        {
            File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        // OMP commits through a linked worktree. Git writes its objects, branch refs, and
        // linked-worktree metadata in the shared bare database; grant its shared group only those
        // mutable paths, never the bare root or remote configuration.
        foreach (var directory in mutableBareRepositoryDirectories)
        {
            var path = Path.Combine(bareRepositoryPath, directory);
            if (Directory.Exists(path))
            {
                MakeDirectoryTreeWritableByOmp(path);
            }
        }
    }

    /// <summary>Makes only the checkout writable by OMP's shared group; bare repositories retain
    /// their owner-only permissions.</summary>
    [SupportedOSPlatform("linux")]
    private void MakeWorktreeWritableByOmp(string worktreePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        MakeAncestorDirectoriesTraversableByOmp(worktreePath, Path.GetDirectoryName(normalizedReposRootPath) ?? normalizedReposRootPath);
        MakeDirectoryTreeWritableByOmp(worktreePath);
    }

    /// <summary>Git stores a linked checkout's HEAD, index, and per-worktree config below the
    /// canonical bare repository. OMP must update that metadata while the bare repository itself
    /// remains outside its writable checkout authority.</summary>
    [SupportedOSPlatform("linux")]
    private static void MakeLinkedWorktreeMetadataWritableByOmp(string barePath, string worktreeId)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var metadataPath = Path.Combine(barePath, "worktrees", worktreeId);
        if (Directory.Exists(metadataPath))
        {
            MakeDirectoryTreeWritableByOmp(metadataPath);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void MakeAncestorDirectoriesTraversableByOmp(string path, string workspaceRootPath)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedWorkspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRootPath));
        var relativePath = Path.GetRelativePath(normalizedWorkspaceRoot, normalizedPath);
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path '{path}' escapes workspace root '{workspaceRootPath}'.");
        }

        for (var directory = Directory.GetParent(normalizedPath); directory is not null; directory = directory.Parent)
        {
            var mode = File.GetUnixFileMode(directory.FullName);
            File.SetUnixFileMode(directory.FullName, (mode | UnixFileMode.GroupExecute) & ~UnixFileMode.GroupWrite);
            if (string.Equals(directory.FullName, normalizedWorkspaceRoot, StringComparison.Ordinal))
            {
                break;
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static void MakeDirectoryTreeWritableByOmp(string directoryPath)
    {
        SetGroupWritableMode(directoryPath, isDirectory: true);
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            if ((File.GetAttributes(entryPath) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (Directory.Exists(entryPath))
            {
                MakeDirectoryTreeWritableByOmp(entryPath);
            }
            else
            {
                SetGroupWritableMode(entryPath, isDirectory: false);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static void SetGroupWritableMode(string path, bool isDirectory)
    {
        var mode = File.GetUnixFileMode(path);
        mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        if (isDirectory || (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
        {
            mode |= UnixFileMode.GroupExecute;
        }

        File.SetUnixFileMode(path, mode);
    }

    private static string? TryGetHost(string? url) => GitUrlHost.TryGetHost(url);

    private static string? TryGetTransportAuthority(string? url) => GitUrlHost.TryGetTransportAuthority(url);

    private static bool TryNormalizeScpLikeSshRemote(string url, out Uri remote)
    {
        var colon = url.IndexOf(':');
        if (colon <= 0 || colon == url.Length - 1 || url.AsSpan(colon).StartsWith("://"))
        {
            remote = null!;
            return false;
        }

        var authority = url[..colon];
        if (authority.Contains('/') || authority.Contains('\\') || string.IsNullOrWhiteSpace(authority))
        {
            remote = null!;
            return false;
        }

        return Uri.TryCreate($"ssh://{authority}/{url[(colon + 1)..]}", UriKind.Absolute, out remote!);
    }

    /// <summary>Returns credentials for <paramref name="authentication"/> only when libgit2's
    /// callback URL transport authority matches <paramref name="expectedTransportAuthority"/>
    /// (specification §11: never forward credentials to an unexpected scheme, host, or port,
    /// including via a followed redirect). A <see langword="null"/>
    /// <paramref name="expectedTransportAuthority"/> means the caller could not determine an
    /// expected authority and no credentials are ever returned.</summary>
    private static CredentialsHandler CredentialsHandlerFor(
        GitAuthentication authentication,
        string? expectedTransportAuthority) =>
        (url, usernameFromUrl, types) =>
        {
            if (expectedTransportAuthority is null ||
                !string.Equals(
                    TryGetTransportAuthority(url),
                    expectedTransportAuthority,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return authentication.Mode switch
            {
                GitAuthenticationMode.ProviderToken or GitAuthenticationMode.Token =>
                    new UsernamePasswordCredentials { Username = authentication.HttpsUsername ?? "x-access-token", Password = authentication.HttpsToken! },
                GitAuthenticationMode.Anonymous => null,
                _ => throw new InvalidOperationException($"Unsupported credential mode '{authentication.Mode}' for HTTPS transport."),
            };
        };

    private static CertificateCheckHandler CertificateCheckHandlerFor(TlsTrust tlsTrust) =>
        (certificate, validByDefault, host) => tlsTrust.Mode switch
        {
            TlsTrustMode.System => validByDefault,
            TlsTrustMode.None => true,
            TlsTrustMode.SystemPlusAdditionalCa => validByDefault || AdditionalCaTrustStore.IsTrusted(certificate, tlsTrust.AdditionalCaCertificatePaths, host),
            TlsTrustMode.Pinned => PinnedCertificateVerifier.Matches(certificate, tlsTrust.Fingerprints),
            _ => false,
        };

    private static void DeleteDirectoryRobust(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteLink(path, attributes);
            return;
        }

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(path))
        {
            attributes = File.GetAttributes(entryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                DeleteLink(entryPath, attributes);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryRobust(entryPath);
            }
            else
            {
                File.SetAttributes(entryPath, FileAttributes.Normal);
                File.Delete(entryPath);
            }
        }

        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path);
    }

    private static void DeleteLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }
}
