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
/// Every bare repository and worktree gets an explicit, empty <c>core.hooksPath</c> so no Git hook
/// can ever execute, and remote-touching git CLI calls run with <c>GIT_CONFIG_NOSYSTEM=1</c> and an
/// isolated <c>HOME</c>/<c>XDG_CONFIG_HOME</c> so ambient host/global Git config is never inherited.
/// </summary>
public sealed class LibGit2SharpRepositoryManager(string reposRootPath) : IGitRepositoryManager
{
    private static readonly string[] mutableBareRepositoryDirectories = ["objects", "refs", "worktrees"];


    public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        var path = BareRepositoryPath(repositoryId);
        if (Repository.IsValid(path))
        {
            HardenBareRepositoryAuthority(path);
            return ValueTask.CompletedTask;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (authentication.Mode == GitAuthenticationMode.Ssh)
        {
            GitSshTransport.CloneBare(cloneUrl, path, authentication);
        }
        else
        {
            var options = new CloneOptions
            {
                IsBare = true,
                Checkout = false,
                FetchOptions = { CredentialsProvider = CredentialsHandlerFor(authentication, TryGetHost(cloneUrl)), CertificateCheck = CertificateCheckHandlerFor(authentication.TlsTrust) },
            };
            Repository.Clone(cloneUrl, path, options);
        }

        DisableHooks(path);
        HardenBareRepositoryAuthority(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        var path = BareRepositoryPath(repositoryId);
        if (authentication.Mode == GitAuthenticationMode.Ssh)
        {
            GitSshTransport.Fetch(path, authentication);
            return ValueTask.CompletedTask;
        }

        using var repo = new Repository(path);
        var remote = repo.Network.Remotes["origin"];
        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
        Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
        {
            CredentialsProvider = CredentialsHandlerFor(authentication, TryGetHost(remote.Url)),
            CertificateCheck = CertificateCheckHandlerFor(authentication.TlsTrust),
        }, logMessage: null);
        return ValueTask.CompletedTask;
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

    public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        foreach (var submodule in repo.Submodules)
        {
            var host = TryGetHost(submodule.Url);
            var authentication = host is null ? null : authenticationResolver(host);

            try
            {
                repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions
                {
                    Init = true,
                    FetchOptions =
                    {
                        CredentialsProvider = CredentialsHandlerFor(authentication ?? GitAuthentication.Anonymous(TlsTrust.System), host),
                        CertificateCheck = CertificateCheckHandlerFor((authentication ?? GitAuthentication.Anonymous(TlsTrust.System)).TlsTrust),
                    },
                });
            }
            catch (LibGit2SharpException) when (authentication is null)
            {
                throw new SubmoduleAuthenticationRequiredException(
                    $"Submodule '{submodule.Name}' at '{submodule.Url}' requires authentication that is not configured for its host.");
            }

            using var submoduleRepo = new Repository(Path.Combine(worktreePath, submodule.Path));
            DisableHooks(submoduleRepo.Info.Path);
        }

        return ValueTask.CompletedTask;
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

        try
        {
            var committer = new Identity(identity.Name, identity.Email);
            var result = repo.Rebase.Start(currentBranch, upstreamBranch, ontoBranch, committer, new RebaseOptions());
            if (result.Status == RebaseStatus.Conflicts)
            {
                repo.Rebase.Abort();
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(true);
        }
        finally
        {
            repo.Branches.Remove(upstreamBranch);
            repo.Branches.Remove(ontoBranch);
        }
    }

    public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken)
    {
        using var repo = new Repository(worktreePath);
        var target = (Commit)repo.Lookup(commit, ObjectType.Commit);
        var merger = new Signature(identity.Name, identity.Email, DateTimeOffset.UtcNow);

        var result = repo.Merge(target, merger, new MergeOptions { CommitOnSuccess = true, FailOnConflict = false });
        return ValueTask.FromResult(result.Status != MergeStatus.Conflicts);
    }

    public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        // OMP can edit a retained worktree, including its .git file. Resolve both the ref and
        // remote from the canonical bare repository, which is never made group-writable, so a
        // worktree cannot redirect a credential-bearing push.
        var bareRepositoryPath = BareRepositoryPath(repositoryId);
        if (authentication.Mode == GitAuthenticationMode.Ssh)
        {
            GitSshTransport.Push(bareRepositoryPath, branchName, authentication);
            return ValueTask.CompletedTask;
        }

        using var repo = new Repository(bareRepositoryPath);
        if (repo.Branches[branchName] is null)
        {
            throw new GitReferenceNotFoundException($"Local branch '{branchName}' was not found.");
        }

        var remote = repo.Network.Remotes["origin"];
        repo.Network.Push(remote, $"refs/heads/{branchName}:refs/heads/{branchName}", new PushOptions
        {
            CredentialsProvider = CredentialsHandlerFor(authentication, TryGetHost(remote.Url)),
            CertificateCheck = CertificateCheckHandlerFor(authentication.TlsTrust),
        });
        return ValueTask.CompletedTask;
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

    public async ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        await GitLfsRunner.MaterializeContentAsync(worktreePath, authentication, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        await GitLfsRunner.UploadObjectsAsync(worktreePath, branchName, authentication, cancellationToken).ConfigureAwait(false);
    }

    private string BareRepositoryPath(string repositoryId) => Path.Combine(reposRootPath, repositoryId);

    private static void DisableHooks(string repositoryOrWorktreePath)
    {
        using var repo = new Repository(repositoryOrWorktreePath);
        var hooksDir = Path.Combine(repo.Info.Path, "issueagent-disabled-hooks");
        Directory.CreateDirectory(hooksDir);
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



    private static void HardenBareRepositoryAuthority(string bareRepositoryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            bareRepositoryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        var configPath = Path.Combine(bareRepositoryPath, "config");
        if (File.Exists(configPath))
        {
            File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
    private static void MakeWorktreeWritableByOmp(string worktreePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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

    private static string? TryGetHost(string url) => GitUrlHost.TryGetHost(url);

    /// <summary>Returns credentials for <paramref name="authentication"/> only when libgit2's
    /// callback URL host matches <paramref name="expectedHost"/> (specification §11: never forward
    /// credentials to an unexpected host, including via a followed redirect). A <see langword="null"/>
    /// <paramref name="expectedHost"/> means the caller could not determine an expected host and no
    /// credentials are ever returned.</summary>
    private static CredentialsHandler CredentialsHandlerFor(GitAuthentication authentication, string? expectedHost) =>
        (url, usernameFromUrl, types) =>
        {
            if (expectedHost is null || !string.Equals(TryGetHost(url), expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                return new DefaultCredentials();
            }

            return authentication.Mode switch
            {
                GitAuthenticationMode.ProviderToken or GitAuthenticationMode.Token =>
                    new UsernamePasswordCredentials { Username = authentication.HttpsUsername ?? "x-access-token", Password = authentication.HttpsToken! },
                GitAuthenticationMode.Anonymous => new DefaultCredentials(),
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
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
