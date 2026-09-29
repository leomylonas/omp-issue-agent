using System.Diagnostics;
using System.Reflection;
using LibGit2Sharp;

namespace IssueAgent.Git.Tests;

public sealed class LibGit2SharpRepositoryManagerTests : IDisposable
{
    private readonly string reposRoot = TempGitFixtures.CreateTempDirectory();
    private static readonly object PathLock = new();

    private readonly List<string> cleanupPaths = [];
    private readonly LibGit2SharpRepositoryManager manager;

    public LibGit2SharpRepositoryManagerTests()
    {
        manager = new LibGit2SharpRepositoryManager(reposRoot);
        cleanupPaths.Add(reposRoot);
    }

    private static string RunGitCli(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }

        return stdout;
    }

    [Fact]
    public void CredentialsHandlerReturnsNoCredentialsForAnonymousAuthority()
    {
        var credentials = InvokeCredentialsHandler(
            GitAuthentication.Anonymous(TlsTrust.System),
            "https://git.trusted.example:443",
            "https://git.trusted.example/repository.git");

        Assert.Null(credentials);
    }

    [Fact]
    public void CredentialsHandlerReturnsNoCredentialsForMismatchedAuthority()
    {
        var credentials = InvokeCredentialsHandler(
            new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" },
            "https://git.trusted.example:443",
            "https://attacker.example/repository.git");

        Assert.Null(credentials);
    }

    [Fact]
    public async Task HttpsRemoteOperationsHonorCancellationBeforeStartingTransfer()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.EnsureBareRepositoryAsync("repo-cancel-clone", remotePath, TempGitFixtures.AnonymousAuthentication(), cancellation.Token).AsTask());

        await manager.EnsureBareRepositoryAsync("repo-cancel-fetch", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.FetchAsync("repo-cancel-fetch", TempGitFixtures.AnonymousAuthentication(), cancellation.Token).AsTask());

        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-cancel-fetch", "wt-cancel", worktreePath, "agent/issue-cancel", baseCommit, CancellationToken.None);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.UpdateSubmodulesAsync("repo-cancel-fetch", worktreePath, _ => null, cancellation.Token).AsTask());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.PushAsync("repo-cancel-fetch", worktreePath, "agent/issue-cancel", TempGitFixtures.AnonymousAuthentication(), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task EnsureBareRepositoryAsyncClonesLazilyAndIsIdempotent()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));

        await manager.EnsureBareRepositoryAsync("repo-1", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        await manager.EnsureBareRepositoryAsync("repo-1", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        Assert.True(Repository.IsValid(Path.Combine(reposRoot, "repo-1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/repository")]
    [InlineData("../repository")]
    [InlineData("github/../repository")]
    public async Task EnsureBareRepositoryAsyncRejectsUnsafeRepositoryIdsBeforeFilesystemAccess(string repositoryId)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.EnsureBareRepositoryAsync(
                repositoryId,
                Path.Combine(reposRoot, "unreachable-remote"),
                TempGitFixtures.AnonymousAuthentication(),
                CancellationToken.None).AsTask());

        Assert.Contains("repositoryId", exception.ParamName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureBareRepositoryAsyncDoesNotMakeDirectoriesAboveNormalizedWorkspaceTraversable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outerDirectory = Track(TempGitFixtures.CreateTempDirectory());
        var workspaceDirectory = Path.Combine(outerDirectory, "workspace");
        var relativeReposRoot = Path.GetRelativePath(
            Environment.CurrentDirectory,
            Path.Combine(workspaceDirectory, "repos"));
        Directory.CreateDirectory(relativeReposRoot);
        File.SetUnixFileMode(
            outerDirectory,
            File.GetUnixFileMode(outerDirectory) & ~UnixFileMode.GroupExecute);
        var relativePathManager = new LibGit2SharpRepositoryManager(relativeReposRoot);
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));

        await relativePathManager.EnsureBareRepositoryAsync(
            "repo-normalized-boundary",
            remotePath,
            TempGitFixtures.AnonymousAuthentication(),
            CancellationToken.None);

        Assert.False((File.GetUnixFileMode(outerDirectory) & UnixFileMode.GroupExecute) != 0);
    }

    [Fact]
    public async Task EnsureBareRepositoryAsyncProtectsCanonicalRemoteConfigurationFromOmpGroupWrites()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        await manager.EnsureBareRepositoryAsync("repo-authority-permissions", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var barePath = Path.Combine(reposRoot, "repo-authority-permissions");
        var configPath = Path.Combine(barePath, "config");
        File.SetUnixFileMode(configPath, File.GetUnixFileMode(configPath) | UnixFileMode.GroupWrite);
        File.SetUnixFileMode(barePath, File.GetUnixFileMode(barePath) | UnixFileMode.GroupWrite);

        await manager.EnsureBareRepositoryAsync("repo-authority-permissions", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        Assert.False((File.GetUnixFileMode(configPath) & UnixFileMode.GroupWrite) != 0);
        Assert.False((File.GetUnixFileMode(barePath) & UnixFileMode.GroupWrite) != 0);
    }

    [Fact]
    public async Task EnsureBareRepositoryAsyncRemovesOmpGroupWriteFromBareRepositoryAncestors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = Track(TempGitFixtures.CreateTempDirectory());
        var workspaceRepos = Path.Combine(workspace, "repos");
        var providerDirectory = Path.Combine(workspaceRepos, "provider");
        Directory.CreateDirectory(providerDirectory);
        foreach (var directory in new[] { workspace, workspaceRepos, providerDirectory })
        {
            File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.GroupWrite);
        }
        var authorityManager = new LibGit2SharpRepositoryManager(workspaceRepos);
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));

        await authorityManager.EnsureBareRepositoryAsync(
            "provider/repository",
            remotePath,
            TempGitFixtures.AnonymousAuthentication(),
            CancellationToken.None);

        foreach (var directory in new[] { workspace, workspaceRepos, providerDirectory })
        {
            Assert.False((File.GetUnixFileMode(directory) & UnixFileMode.GroupWrite) != 0);
            Assert.True((File.GetUnixFileMode(directory) & UnixFileMode.GroupExecute) != 0);
        }
    }

    [Fact]
    public async Task FetchAsyncRestoresOmpPermissionsForNewBareRepositoryEntries()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        await manager.EnsureBareRepositoryAsync("repo-fetch-permissions", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var barePath = Path.Combine(reposRoot, "repo-fetch-permissions");
        var refsPath = Path.Combine(barePath, "refs");
        File.SetUnixFileMode(refsPath, File.GetUnixFileMode(refsPath) & ~(UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute));

        await manager.FetchAsync("repo-fetch-permissions", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        var mode = File.GetUnixFileMode(refsPath);
        Assert.True(
            (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)) ==
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute));
    }

    [Fact]
    public async Task ResolveBranchCommitAsyncReturnsExactShaAndThrowsForMissingBranch()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var expectedSha));
        await manager.EnsureBareRepositoryAsync("repo-2", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        var resolved = await manager.ResolveBranchCommitAsync("repo-2", "main", CancellationToken.None);

        Assert.Equal(expectedSha, resolved);
        await Assert.ThrowsAsync<GitReferenceNotFoundException>(() =>
            manager.ResolveBranchCommitAsync("repo-2", "does-not-exist", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task TryResolveRemoteBranchCommitAsyncDoesNotTreatBareLocalHeadsAsAuthoritative()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        const string repositoryId = "repo-authoritative-head";
        await manager.EnsureBareRepositoryAsync(repositoryId, remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var barePath = Path.Combine(reposRoot, repositoryId);

        RunGitCli(barePath, "update-ref", "-d", "refs/remotes/origin/main");
        RunGitCli(barePath, "config", "remote.origin.fetch", "+refs/heads/*:refs/heads/*");

        Assert.Null(await manager.TryResolveRemoteBranchCommitAsync(repositoryId, "main", CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsyncBringsNewRemoteCommits()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        await manager.EnsureBareRepositoryAsync("repo-3", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        string newSha;
        using (var remoteRepo = new Repository(remotePath))
        {
            File.WriteAllText(Path.Combine(remotePath, "second.txt"), "more content\n");
            Commands.Stage(remoteRepo, "second.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            newSha = remoteRepo.Commit("Second commit", signature, signature).Sha;
        }

        await manager.FetchAsync("repo-3", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var resolved = await manager.ResolveBranchCommitAsync("repo-3", "main", CancellationToken.None);

        Assert.Equal(newSha, resolved);
    }

    [Fact]
    public async Task IsAncestorAsyncDistinguishesFastForwardFromRewrite()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseSha));
        await manager.EnsureBareRepositoryAsync("repo-ancestry", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        string descendantSha;
        using (var remoteRepo = new Repository(remotePath))
        {
            File.WriteAllText(Path.Combine(remotePath, "second.txt"), "more content\n");
            Commands.Stage(remoteRepo, "second.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            descendantSha = remoteRepo.Commit("Second commit", signature, signature).Sha;
        }

        await manager.FetchAsync("repo-ancestry", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        Assert.True(await manager.IsAncestorAsync("repo-ancestry", baseSha, descendantSha, CancellationToken.None));
        Assert.False(await manager.IsAncestorAsync("repo-ancestry", descendantSha, baseSha, CancellationToken.None));
    }

    [Fact]
    public async Task CreateWorktreeAsyncChecksOutNewBranchAtBaseCommit()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-4", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));

        await manager.CreateWorktreeAsync("repo-4", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(worktreePath, "README.md")));
        Assert.Equal(baseCommit, await manager.GetHeadCommitAsync("repo-4", worktreePath, CancellationToken.None));
        Assert.False(await manager.HasUncommittedChangesAsync("repo-4", worktreePath, CancellationToken.None));
    }

    [Fact]
    public async Task RenameWorktreeBranchAsyncTreatsAnAlreadyRenamedWorktreeAsRecoverySuccess()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-rename-recovery", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        const string originalBranch = "agent/issue-1-original";
        const string suggestedBranch = "agent/issue-1-suggested";
        await manager.CreateWorktreeAsync(
            "repo-rename-recovery",
            "wt-rename-recovery",
            worktreePath,
            originalBranch,
            baseCommit,
            CancellationToken.None);

        await manager.RenameWorktreeBranchAsync(
            "repo-rename-recovery",
            worktreePath,
            originalBranch,
            suggestedBranch,
            CancellationToken.None);
        await manager.RenameWorktreeBranchAsync(
            "repo-rename-recovery",
            worktreePath,
            originalBranch,
            suggestedBranch,
            CancellationToken.None);

        using var worktree = new Repository(worktreePath);
        Assert.Equal(suggestedBranch, worktree.Head.FriendlyName);
    }

    [Fact]
    public async Task CreateWorktreeAsyncMakesCheckoutAndRequiredBareGitMetadataGroupWritableForOmp()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-group-permissions", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var bareHeadPath = Path.Combine(reposRoot, "repo-group-permissions", "HEAD");
        var bareHeadMode = File.GetUnixFileMode(bareHeadPath);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));

        await manager.CreateWorktreeAsync("repo-group-permissions", "wt-permissions", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        var checkoutMode = File.GetUnixFileMode(Path.Combine(worktreePath, "README.md"));
        var checkoutDirectoryMode = File.GetUnixFileMode(worktreePath);
        Assert.True((checkoutMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite)) == (UnixFileMode.GroupRead | UnixFileMode.GroupWrite));
        Assert.True((checkoutDirectoryMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)) == (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute));
        var barePath = Path.Combine(reposRoot, "repo-group-permissions");
        var linkedMetadataPath = Path.Combine(barePath, "worktrees", "wt-permissions");
        var linkedIndexMode = File.GetUnixFileMode(Path.Combine(linkedMetadataPath, "index"));
        Assert.True((linkedIndexMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite)) == (UnixFileMode.GroupRead | UnixFileMode.GroupWrite));
        foreach (var mutableDirectory in new[] { "objects", "refs", "worktrees" })
        {
            var mode = File.GetUnixFileMode(Path.Combine(barePath, mutableDirectory));
            Assert.True(
                (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)) ==
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute));
        }
        Assert.False((File.GetUnixFileMode(barePath) & UnixFileMode.GroupWrite) != 0);
        var configMode = File.GetUnixFileMode(Path.Combine(barePath, "config"));
        Assert.True((configMode & UnixFileMode.GroupRead) != 0);
        Assert.False((configMode & UnixFileMode.GroupWrite) != 0);
        Assert.True((File.GetUnixFileMode(reposRoot) & UnixFileMode.GroupExecute) != 0);
        Assert.True((File.GetUnixFileMode(Path.GetDirectoryName(worktreePath)!) & UnixFileMode.GroupExecute) != 0);
        Assert.Equal(bareHeadMode, File.GetUnixFileMode(bareHeadPath));
    }

    [Fact]
    public async Task CreateWorktreeAsyncConfiguresHostOwnedHooksDirectoryForGitCli()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-hooks-permissions", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var barePath = Path.Combine(reposRoot, "repo-hooks-permissions");
        var legacyHooksPath = Path.Combine(barePath, "old-disabled-hooks");
        Directory.CreateDirectory(legacyHooksPath);
        File.SetUnixFileMode(legacyHooksPath, File.GetUnixFileMode(legacyHooksPath) | UnixFileMode.GroupWrite);
        using (var bareRepository = new Repository(barePath))
        {
            bareRepository.Config.Set("core.hooksPath", legacyHooksPath, ConfigurationLevel.Local);
        }

        await manager.EnsureBareRepositoryAsync("repo-hooks-permissions", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-hooks-permissions", "wt-hooks", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        var hooksPath = Path.Combine(barePath, "issueagent-disabled-hooks");

        var sentinelPath = Path.Combine(worktreePath, "pre-commit-fired");
        var realHookPath = Path.Combine(barePath, "hooks", "pre-commit");
        File.WriteAllText(realHookPath, $"#!/bin/sh\ntouch \"{sentinelPath}\"\n");
        File.SetUnixFileMode(realHookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal(hooksPath, RunGitCli(worktreePath, "config", "--get", "core.hooksPath").Trim());
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(hooksPath));

        File.WriteAllText(Path.Combine(worktreePath, "new-file.txt"), "content");
        RunGitCli(worktreePath, "add", "new-file.txt");
        RunGitCli(worktreePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Verify hooks stay disabled");

        Assert.False(File.Exists(sentinelPath));
    }

    [Fact]
    public async Task CreateWorktreeAsyncDoesNotFollowCheckoutSymlinksWhenAdjustingGroupPermissions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-symlink-permissions", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-symlink-permissions", "wt-symlink", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        var secretPath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "provider-secret"));
        File.WriteAllText(secretPath, "secret");
        File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(Path.Combine(worktreePath, "provider-secret"), secretPath);

        await manager.CreateWorktreeAsync("repo-symlink-permissions", "wt-symlink", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(secretPath));
    }

    [Fact]
    public async Task CreateWorktreeAsyncRepairsStaleRegistrationAfterDirectoryRemoval()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-stale", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-stale", "wt-stale", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        Directory.Delete(worktreePath, recursive: true);

        await manager.CreateWorktreeAsync("repo-stale", "wt-stale", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(worktreePath, "README.md")));
        using var repository = new LibGit2Sharp.Repository(Path.Combine(reposRoot, "repo-stale"));
        Assert.Single(repository.Worktrees, worktree => worktree.Name == "wt-stale");
    }

    [Fact]
    public async Task ResetWorktreeAsyncDiscardsUncommittedChanges()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-5", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-5", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        File.WriteAllText(Path.Combine(worktreePath, "scratch.txt"), "dirty");
        Assert.True(await manager.HasUncommittedChangesAsync("repo-5", worktreePath, CancellationToken.None));

        await manager.ResetWorktreeAsync("repo-5", worktreePath, baseCommit, CancellationToken.None);

        Assert.False(await manager.HasUncommittedChangesAsync("repo-5", worktreePath, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(worktreePath, "scratch.txt")));
    }

    [Fact]
    public async Task GitHooksNeverExecuteInBareRepositoryOrWorktree()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-6", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-6", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        var sentinelPath = Path.Combine(worktreePath, "sentinel-fired.txt");
        var realHooksDir = Path.Combine(reposRoot, "repo-6", "hooks");
        Directory.CreateDirectory(realHooksDir);
        var preCommitHook = Path.Combine(realHooksDir, "pre-commit");
        File.WriteAllText(preCommitHook, $"#!/bin/sh\necho fired > \"{sentinelPath}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(preCommitHook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using (var repo = new Repository(worktreePath))
        {
            File.WriteAllText(Path.Combine(worktreePath, "new-file.txt"), "content");
            Commands.Stage(repo, "new-file.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("Trigger potential hook", signature, signature);
        }

        Assert.False(File.Exists(sentinelPath));
    }

    [Fact]
    public async Task TryMergeAsyncSucceedsForNonConflictingChangesAndReportsConflicts()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-7", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-7", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        string nonConflictingCommit;
        using (var remoteRepo = new Repository(remotePath))
        {
            File.WriteAllText(Path.Combine(remotePath, "unrelated.txt"), "unrelated content\n");
            Commands.Stage(remoteRepo, "unrelated.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            nonConflictingCommit = remoteRepo.Commit("Unrelated change", signature, signature).Sha;
        }

        await manager.FetchAsync("repo-7", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var merged = await manager.TryMergeAsync("repo-7", worktreePath, nonConflictingCommit, TempGitFixtures.TestIdentity, CancellationToken.None);

        Assert.True(merged);
        Assert.True(File.Exists(Path.Combine(worktreePath, "unrelated.txt")));
    }

    [Fact]
    public async Task TryMergeAsyncReturnsFalseOnConflict()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-8", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-8", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        using (var worktreeRepo = new Repository(worktreePath))
        {
            File.WriteAllText(Path.Combine(worktreePath, "README.md"), "local change\n");
            Commands.Stage(worktreeRepo, "README.md");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            worktreeRepo.Commit("Local conflicting change", signature, signature);
        }

        string conflictingCommit;
        using (var remoteRepo = new Repository(remotePath))
        {
            File.WriteAllText(Path.Combine(remotePath, "README.md"), "remote change\n");
            Commands.Stage(remoteRepo, "README.md");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            conflictingCommit = remoteRepo.Commit("Remote conflicting change", signature, signature).Sha;
        }

        await manager.FetchAsync("repo-8", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var merged = await manager.TryMergeAsync("repo-8", worktreePath, conflictingCommit, TempGitFixtures.TestIdentity, CancellationToken.None);

        Assert.False(merged);
    }

    [Fact]
    public async Task TryRebaseOntoAsyncReplaysCommitsOntoNewBase()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-9", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-9", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        using (var worktreeRepo = new Repository(worktreePath))
        {
            File.WriteAllText(Path.Combine(worktreePath, "agent-work.txt"), "agent change\n");
            Commands.Stage(worktreeRepo, "agent-work.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            worktreeRepo.Commit("Agent work", signature, signature);
        }

        string newBaseCommit;
        using (var remoteRepo = new Repository(remotePath))
        {
            File.WriteAllText(Path.Combine(remotePath, "unrelated.txt"), "unrelated\n");
            Commands.Stage(remoteRepo, "unrelated.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            newBaseCommit = remoteRepo.Commit("New base commit", signature, signature).Sha;
        }

        await manager.FetchAsync("repo-9", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var rebased = await manager.TryRebaseOntoAsync("repo-9", worktreePath, newBaseCommit, TempGitFixtures.TestIdentity, CancellationToken.None);

        Assert.True(rebased);
        Assert.True(File.Exists(Path.Combine(worktreePath, "unrelated.txt")));
        Assert.True(File.Exists(Path.Combine(worktreePath, "agent-work.txt")));
    }

    [Fact]
    public async Task TryRebaseOntoAsyncPreservesConflictStateForResolution()
    {
        var remotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-rebase-conflict", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-rebase-conflict", "wt-rebase-conflict", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        using (var worktreeRepo = new Repository(worktreePath))
        {
            File.WriteAllText(Path.Combine(worktreePath, "README.md"), "agent change\n");
            Commands.Stage(worktreeRepo, "README.md");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            worktreeRepo.Commit("Agent change", signature, signature);
        }

        var targetWorktreePath = Track(TempGitFixtures.CreateTempDirectory());
        Directory.Delete(targetWorktreePath);
        Repository.Clone(remotePath, targetWorktreePath);
        string targetCommit;
        using (var targetRepository = new Repository(targetWorktreePath))
        {
            File.WriteAllText(Path.Combine(targetWorktreePath, "README.md"), "target change\n");
            Commands.Stage(targetRepository, "README.md");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            targetCommit = targetRepository.Commit("Target change", signature, signature).Sha;
            targetRepository.Network.Push(targetRepository.Network.Remotes["origin"], $"{targetRepository.Head.CanonicalName}:{targetRepository.Head.CanonicalName}");
        }

        await manager.FetchAsync("repo-rebase-conflict", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        Assert.False(await manager.TryRebaseOntoAsync("repo-rebase-conflict", worktreePath, targetCommit, TempGitFixtures.TestIdentity, CancellationToken.None));
        using (var conflictedRepository = new Repository(worktreePath))
        {
            Assert.Contains(conflictedRepository.RetrieveStatus(), entry => entry.State.HasFlag(FileStatus.Conflicted));
        }

        File.WriteAllText(Path.Combine(worktreePath, "README.md"), "resolved change\n");
        RunGitCli(worktreePath, "add", "README.md");
        RunGitCli(worktreePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Resolve target conflict");

        var resolvedHead = await manager.GetHeadCommitAsync("repo-rebase-conflict", worktreePath, CancellationToken.None);
        Assert.True(await manager.IsAncestorAsync("repo-rebase-conflict", targetCommit, resolvedHead, CancellationToken.None));

        await manager.PushAsync("repo-rebase-conflict", worktreePath, "agent/issue-1", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        using var publishedRepository = new Repository(remotePath);
        Assert.Equal(
            targetCommit,
            publishedRepository.ObjectDatabase.FindMergeBase(
                publishedRepository.Lookup<Commit>(targetCommit),
                publishedRepository.Branches["agent/issue-1"].Tip)?.Sha);
    }

    [Fact]
    public async Task PushAsyncNeverForcesAndUpdatesRemoteBranch()
    {
        var bareRemotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-10", bareRemotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-10", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        string commitSha;
        using (var worktreeRepo = new Repository(worktreePath))
        {
            File.WriteAllText(Path.Combine(worktreePath, "published.txt"), "published change\n");
            Commands.Stage(worktreeRepo, "published.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            commitSha = worktreeRepo.Commit("Publish", signature, signature).Sha;
        }

        await manager.PushAsync("repo-10", worktreePath, "agent/issue-1", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        using var bareRepo = new Repository(bareRemotePath);
        var remoteBranch = bareRepo.Branches["agent/issue-1"];
        Assert.NotNull(remoteBranch);
        Assert.Equal(commitSha, remoteBranch.Tip.Sha);
    }

    [Fact]
    public async Task PushAsyncRejectsDirtyWorktreeWithoutPublishing()
    {
        var bareRemotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-push-dirty", bareRemotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-push-dirty", "wt-push-dirty", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        File.WriteAllText(Path.Combine(worktreePath, "uncommitted.txt"), "must not publish\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PushAsync("repo-push-dirty", worktreePath, "agent/issue-1", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None).AsTask());

        using var bareRepo = new Repository(bareRemotePath);
        Assert.Null(bareRepo.Branches["agent/issue-1"]);
    }

    [Fact]
    public async Task PushAsyncUsesTheCanonicalBareRepositoryWhenOmpReplacesWorktreeGitMetadata()
    {
        var bareRemotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-push-authority", bareRemotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-push-authority", "wt-push-authority", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        using (var worktreeRepo = new Repository(worktreePath))
        {
            File.WriteAllText(Path.Combine(worktreePath, "published.txt"), "published change\n");
            Commands.Stage(worktreeRepo, "published.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            worktreeRepo.Commit("Publish", signature, signature);
        }

        var attackerRepositoryPath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "attacker.git"));
        Repository.Init(attackerRepositoryPath, isBare: true);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), $"gitdir: {attackerRepositoryPath}\n");

        await manager.PushAsync("repo-push-authority", worktreePath, "agent/issue-1", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        using var bareRepo = new Repository(bareRemotePath);
        Assert.NotNull(bareRepo.Branches["agent/issue-1"]);
    }

    [Fact]
    public async Task RemoveWorktreeAsyncDeletesWorktreeDirectoryAndAdminMetadata()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-11", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-11", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        await manager.RemoveWorktreeAsync("repo-11", "wt-1", worktreePath, CancellationToken.None);

        Assert.False(Directory.Exists(worktreePath));
        using var repo = new Repository(Path.Combine(reposRoot, "repo-11"));
        Assert.DoesNotContain(repo.Worktrees, w => w.Name == "wt-1");
    }

    [Fact]
    public async Task RemoveWorktreeAsyncDoesNotTraverseSymbolicLinks()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-cleanup-symlink", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-cleanup-symlink", "wt-cleanup-symlink", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        var outsideDirectory = Track(TempGitFixtures.CreateTempDirectory());
        var sentinelPath = Path.Combine(outsideDirectory, "sentinel.txt");
        File.WriteAllText(sentinelPath, "must survive worktree cleanup");
        Directory.CreateSymbolicLink(Path.Combine(worktreePath, "outside"), outsideDirectory);

        await manager.RemoveWorktreeAsync("repo-cleanup-symlink", "wt-cleanup-symlink", worktreePath, CancellationToken.None);

        Assert.True(File.Exists(sentinelPath));
        Assert.False(Directory.Exists(worktreePath));
    }

    [Fact]
    public async Task RemoveLocalBranchAsyncDeletesOnlyLocalBranch()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-12", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-12", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        await manager.RemoveWorktreeAsync("repo-12", "wt-1", worktreePath, CancellationToken.None);

        await manager.RemoveLocalBranchAsync("repo-12", "agent/issue-1", CancellationToken.None);

        using var repo = new Repository(Path.Combine(reposRoot, "repo-12"));
        Assert.Null(repo.Branches["agent/issue-1"]);
        Assert.True(Directory.Exists(remotePath));
    }

    [Fact]
    public async Task UpdateSubmodulesAsyncInitializesLocalPathSubmoduleAnonymously()
    {
        var submoduleSourcePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        var remotePath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(remotePath);
        RunGitCli(remotePath, "-c", "protocol.file.allow=always", "submodule", "add", submoduleSourcePath, "lib/dependency");
        using (var remoteRepo = new Repository(remotePath))
        {
            Commands.Stage(remoteRepo, "*");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            remoteRepo.Commit("Add submodule", signature, signature);
        }

        var baseCommit = new Repository(remotePath).Head.Tip.Sha;
        await manager.EnsureBareRepositoryAsync("repo-13", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-13", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        await manager.UpdateSubmodulesAsync("repo-13", worktreePath, _ => null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(worktreePath, "lib", "dependency", "README.md")));
    }

    [Fact]
    public void ResolveSubmoduleUrlPreservesScpTransportForRelativeUrl()
    {
        var method = typeof(LibGit2SharpRepositoryManager).GetMethod(
            "ResolveSubmoduleUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var resolved = Assert.IsType<string>(
            method.Invoke(null, ["../shared.git", "git@github.example:octo/widgets.git"]));

        Assert.Equal("git@github.example:octo/shared.git", resolved);
    }

    [Fact]
    public async Task UpdateSubmodulesAsyncInitializesSshSubmoduleThroughGitSshTransport()
    {
        var submoduleSourcePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        var remotePath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(remotePath);
        RunGitCli(remotePath, "-c", "protocol.file.allow=always", "submodule", "add", submoduleSourcePath, "lib/dependency");
        RunGitCli(remotePath, "config", "-f", ".gitmodules", "submodule.lib/dependency.url", $"git@example.test:{submoduleSourcePath}");
        using (var remoteRepository = new Repository(remotePath))
        {
            Commands.Stage(remoteRepository, "*");
            remoteRepository.Commit("Add SSH submodule", new Signature("Test", "test@example.com", DateTimeOffset.UtcNow), new Signature("Test", "test@example.com", DateTimeOffset.UtcNow));
        }

        var baseCommit = new Repository(remotePath).Head.Tip.Sha;
        await manager.EnsureBareRepositoryAsync("repo-ssh-submodule", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-ssh-submodule", "wt-ssh-submodule", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        var sshAuthentication = new GitAuthentication
        {
            Mode = GitAuthenticationMode.Ssh,
            SshPrivateKey = "test private key",
            SshTrust = new SshTrust { Mode = SshHostVerificationMode.None },
        };

        WithFakeCommand("ssh", $"#!/bin/sh\nexec git-upload-pack \"{submoduleSourcePath}\"\n", () =>
            manager.UpdateSubmodulesAsync("repo-ssh-submodule", worktreePath, remoteUrl => GitUrlHost.TryGetHost(remoteUrl) == "example.test" ? sshAuthentication : null, CancellationToken.None).AsTask().GetAwaiter().GetResult());

        Assert.True(File.Exists(Path.Combine(worktreePath, "lib", "dependency", "README.md")));
    }

    [Fact]
    public async Task UpdateSubmodulesAsyncRejectsSshAuthenticationForNonSshSubmoduleUrl()
    {
        var submoduleSourcePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        var remotePath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(remotePath);
        RunGitCli(remotePath, "-c", "protocol.file.allow=always", "submodule", "add", submoduleSourcePath, "lib/dependency");
        RunGitCli(remotePath, "config", "-f", ".gitmodules", "submodule.lib/dependency.url", "https://example.test/dependency.git");
        using (var remoteRepository = new Repository(remotePath))
        {
            Commands.Stage(remoteRepository, "*");
            remoteRepository.Commit("Add incompatible submodule", new Signature("Test", "test@example.com", DateTimeOffset.UtcNow), new Signature("Test", "test@example.com", DateTimeOffset.UtcNow));
        }

        var baseCommit = new Repository(remotePath).Head.Tip.Sha;
        await manager.EnsureBareRepositoryAsync("repo-incompatible-ssh-submodule", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-incompatible-ssh-submodule", "wt-incompatible-ssh-submodule", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        var sshAuthentication = new GitAuthentication
        {
            Mode = GitAuthenticationMode.Ssh,
            SshPrivateKey = "test private key",
            SshTrust = new SshTrust { Mode = SshHostVerificationMode.None },
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UpdateSubmodulesAsync("repo-incompatible-ssh-submodule", worktreePath, remoteUrl => GitUrlHost.TryGetHost(remoteUrl) == "example.test" ? sshAuthentication : null, CancellationToken.None).AsTask());

        Assert.Contains("SSH endpoint is ambiguous", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(worktreePath, "lib", "dependency", ".git")));
    }


    [Fact]
    public async Task UpdateSubmodulesAsyncRecursivelyInitializesNestedSubmodules()
    {
        var leafPath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        var intermediatePath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(intermediatePath);
        RunGitCli(intermediatePath, "-c", "protocol.file.allow=always", "submodule", "add", leafPath, "nested/leaf");
        using (var intermediateRepository = new Repository(intermediatePath))
        {
            Commands.Stage(intermediateRepository, "*");
            intermediateRepository.Commit("Add nested submodule", new Signature("Test", "test@example.com", DateTimeOffset.UtcNow), new Signature("Test", "test@example.com", DateTimeOffset.UtcNow));
        }

        var remotePath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(remotePath);
        RunGitCli(remotePath, "-c", "protocol.file.allow=always", "submodule", "add", intermediatePath, "lib/intermediate");
        using (var remoteRepository = new Repository(remotePath))
        {
            Commands.Stage(remoteRepository, "*");
            remoteRepository.Commit("Add submodule", new Signature("Test", "test@example.com", DateTimeOffset.UtcNow), new Signature("Test", "test@example.com", DateTimeOffset.UtcNow));
        }

        var baseCommit = new Repository(remotePath).Head.Tip.Sha;
        await manager.EnsureBareRepositoryAsync("repo-recursive-submodules", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-recursive-submodules", "wt-recursive-submodules", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        await manager.UpdateSubmodulesAsync("repo-recursive-submodules", worktreePath, _ => null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(worktreePath, "lib", "intermediate", "nested", "leaf", "README.md")));
    }

    [Fact]
    public async Task UpdateSubmodulesAsyncRejectsAPathThatTraversesASymbolicLink()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var submoduleSourcePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        var remotePath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(remotePath);
        RunGitCli(remotePath, "-c", "protocol.file.allow=always", "submodule", "add", submoduleSourcePath, "dependency");
        using (var remoteRepository = new Repository(remotePath))
        {
            Commands.Stage(remoteRepository, "*");
            remoteRepository.Commit("Add submodule", new Signature("Test", "test@example.com", DateTimeOffset.UtcNow), new Signature("Test", "test@example.com", DateTimeOffset.UtcNow));
        }

        var baseCommit = new Repository(remotePath).Head.Tip.Sha;
        await manager.EnsureBareRepositoryAsync("repo-submodule-symlink", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-submodule-symlink", "wt-submodule-symlink", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        var outsidePath = Track(TempGitFixtures.CreateTempDirectory());
        Directory.Delete(Path.Combine(worktreePath, "dependency"));
        File.CreateSymbolicLink(Path.Combine(worktreePath, "dependency"), outsidePath);


        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UpdateSubmodulesAsync("repo-submodule-symlink", worktreePath, _ => null, CancellationToken.None).AsTask());

        Assert.Empty(Directory.EnumerateFileSystemEntries(outsidePath));
    }

    [Fact]
    public async Task PublishChangedSubmodulesAsyncPushesChangedGitlinkCommitBeforeParentPublication()
    {
        var childRemotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out _));
        var parentPath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(parentPath);
        RunGitCli(parentPath, "-c", "protocol.file.allow=always", "submodule", "add", childRemotePath, "dependencies/child");
        RunGitCli(parentPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-am", "Add child submodule");
        var baseCommit = new Repository(parentPath).Head.Tip.Sha;

        await manager.EnsureBareRepositoryAsync("publish-submodule", parentPath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("publish-submodule", "publish-submodule-wt", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        await manager.UpdateSubmodulesAsync("publish-submodule", worktreePath, _ => null, CancellationToken.None);

        var childPath = Path.Combine(worktreePath, "dependencies", "child");
        File.WriteAllText(Path.Combine(childPath, "published.txt"), "child publication");
        RunGitCli(childPath, "add", "published.txt");
        RunGitCli(childPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Update child");
        var childCommit = new Repository(childPath).Head.Tip.Sha;
        RunGitCli(worktreePath, "add", "dependencies/child");
        RunGitCli(worktreePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Update child gitlink");

        await manager.PublishChangedSubmodulesAsync(
            "publish-submodule",
            worktreePath,
            baseCommit,
            "agent/issue-1",
            TempGitFixtures.AnonymousAuthentication(),
            _ => null,
            CancellationToken.None);

        using var childRemote = new Repository(childRemotePath);
        Assert.Equal(childCommit, childRemote.Branches["agent/issue-1"]!.Tip.Sha);
    }

    [Fact]
    public async Task PublishChangedSubmodulesAsyncRejectsOmpMutatedOriginUsingCommittedGitmodulesUrl()
    {
        var childRemotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out _));
        var parentPath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(parentPath);
        RunGitCli(parentPath, "-c", "protocol.file.allow=always", "submodule", "add", childRemotePath, "dependencies/child");
        RunGitCli(parentPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-am", "Add child submodule");
        var baseCommit = new Repository(parentPath).Head.Tip.Sha;

        await manager.EnsureBareRepositoryAsync("reject-mutated-submodule-origin", parentPath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("reject-mutated-submodule-origin", "reject-mutated-submodule-origin-wt", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        await manager.UpdateSubmodulesAsync("reject-mutated-submodule-origin", worktreePath, _ => null, CancellationToken.None);

        var childPath = Path.Combine(worktreePath, "dependencies", "child");
        File.WriteAllText(Path.Combine(childPath, "published.txt"), "child publication");
        RunGitCli(childPath, "add", "published.txt");
        RunGitCli(childPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Update child");
        RunGitCli(worktreePath, "config", "-f", ".gitmodules", "submodule.dependencies/child.url", "https://trusted.example/dependencies/child.git");
        RunGitCli(worktreePath, "add", ".gitmodules", "dependencies/child");
        RunGitCli(worktreePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Update child gitlink");
        RunGitCli(childPath, "remote", "set-url", "origin", "https://attacker.example/exfiltrate.git");
        var credentialHosts = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PublishChangedSubmodulesAsync(
                "reject-mutated-submodule-origin",
                worktreePath,
                baseCommit,
                "agent/issue-1",
                TempGitFixtures.AnonymousAuthentication(),
                host =>
                {
                    credentialHosts.Add(host);
                    return TempGitFixtures.AnonymousAuthentication();
                },
                CancellationToken.None).AsTask());

        Assert.Contains("does not match its committed .gitmodules URL", exception.Message, StringComparison.Ordinal);
        Assert.Empty(credentialHosts);
    }

    [Fact]
    public async Task PublishChangedSubmodulesAsyncSkipsUnchangedNestedGitlinks()
    {
        var publicDependencyRemotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out _));
        var parentSourcePath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(parentSourcePath);
        RunGitCli(parentSourcePath, "-c", "protocol.file.allow=always", "submodule", "add", publicDependencyRemotePath, "dependencies/public");
        RunGitCli(parentSourcePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-am", "Add public dependency");

        var parentRemotePath = Track(TempGitFixtures.CreateTempDirectory());
        Directory.Delete(parentRemotePath);
        Repository.Clone(parentSourcePath, parentRemotePath, new CloneOptions { IsBare = true });

        var rootRepositoryPath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(rootRepositoryPath);
        RunGitCli(rootRepositoryPath, "-c", "protocol.file.allow=always", "submodule", "add", parentRemotePath, "dependencies/parent");
        RunGitCli(rootRepositoryPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-am", "Add parent submodule");
        var baseCommit = new Repository(rootRepositoryPath).Head.Tip.Sha;

        await manager.EnsureBareRepositoryAsync("skip-unchanged-nested-submodule", rootRepositoryPath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("skip-unchanged-nested-submodule", "skip-unchanged-nested-submodule-wt", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        await manager.UpdateSubmodulesAsync("skip-unchanged-nested-submodule", worktreePath, _ => null, CancellationToken.None);

        var parentPath = Path.Combine(worktreePath, "dependencies", "parent");
        File.WriteAllText(Path.Combine(parentPath, "published.txt"), "parent publication");
        RunGitCli(parentPath, "add", "published.txt");
        RunGitCli(parentPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Update parent");
        var parentCommit = new Repository(parentPath).Head.Tip.Sha;
        RunGitCli(worktreePath, "add", "dependencies/parent");
        RunGitCli(worktreePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Update parent gitlink");

        await manager.PublishChangedSubmodulesAsync(
            "skip-unchanged-nested-submodule",
            worktreePath,
            baseCommit,
            "agent/issue-1",
            TempGitFixtures.AnonymousAuthentication(),
            _ => null,
            CancellationToken.None);

        using var parentRemote = new Repository(parentRemotePath);
        using var publicDependencyRemote = new Repository(publicDependencyRemotePath);
        Assert.Equal(parentCommit, parentRemote.Branches["agent/issue-1"]!.Tip.Sha);
        Assert.Null(publicDependencyRemote.Branches["agent/issue-1"]);
    }

    [Fact]
    public async Task PublishChangedSubmodulesAsyncRejectsAnUninitializedChangedGitlink()
    {
        var childRemotePath = Track(TempGitFixtures.CreateBareRemoteRepository(out _));
        var parentPath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(parentPath);
        RunGitCli(parentPath, "-c", "protocol.file.allow=always", "submodule", "add", childRemotePath, "dependencies/child");
        RunGitCli(parentPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-am", "Add child submodule");
        var baseCommit = new Repository(parentPath).Head.Tip.Sha;

        await manager.EnsureBareRepositoryAsync("reject-uninitialized-submodule", parentPath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("reject-uninitialized-submodule", "reject-uninitialized-submodule-wt", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        await manager.UpdateSubmodulesAsync("reject-uninitialized-submodule", worktreePath, _ => null, CancellationToken.None);

        var childPath = Path.Combine(worktreePath, "dependencies", "child");
        File.WriteAllText(Path.Combine(childPath, "unpublished.txt"), "unpublished");
        RunGitCli(childPath, "add", "unpublished.txt");
        RunGitCli(childPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Unpublished child");
        RunGitCli(worktreePath, "add", "dependencies/child");
        RunGitCli(worktreePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Update child gitlink");
        Directory.Delete(childPath, recursive: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PublishChangedSubmodulesAsync(
                "reject-uninitialized-submodule",
                worktreePath,
                baseCommit,
                "agent/issue-1",
                TempGitFixtures.AnonymousAuthentication(),
                _ => null,
                CancellationToken.None).AsTask());

        Assert.Contains("not initialized", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureBareRepositoryAsyncRejectsCachedOriginThatDiffersFromConfiguredCloneUrl()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        await manager.EnsureBareRepositoryAsync("origin-validation", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var barePath = Path.Combine(reposRoot, "origin-validation");
        RunGitCli(barePath, "remote", "set-url", "origin", Path.Combine(reposRoot, "unexpected.git"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureBareRepositoryAsync("origin-validation", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None).AsTask());

        Assert.Contains("origin URL different", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureBareRepositoryAsyncMigratesLegacyOriginRefspecBeforeReuse()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));
        await manager.EnsureBareRepositoryAsync("legacy-refspec", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var barePath = Path.Combine(reposRoot, "legacy-refspec");
        RunGitCli(barePath, "config", "remote.origin.fetch", "+refs/heads/*:refs/heads/*");

        await manager.EnsureBareRepositoryAsync("legacy-refspec", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        using var repository = new Repository(barePath);
        var origin = repository.Network.Remotes["origin"];
        Assert.Equal(
            ["+refs/heads/*:refs/remotes/origin/*"],
            origin.FetchRefSpecs.Select(spec => spec.Specification));
    }

    [Theory]
    [InlineData(GitAuthenticationMode.Ssh, "https://git.example/repository.git")]
    [InlineData(GitAuthenticationMode.Token, "ssh://git@git.example/repository.git")]
    public async Task EnsureBareRepositoryAsyncRejectsAuthenticationModeThatDoesNotMatchCloneUrl(
        GitAuthenticationMode mode,
        string cloneUrl)
    {
        var authentication = new GitAuthentication { Mode = mode };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureBareRepositoryAsync("scheme-mismatch", cloneUrl, authentication, CancellationToken.None).AsTask());
    }

    private static object? InvokeCredentialsHandler(
        GitAuthentication authentication,
        string? expectedTransportAuthority,
        string callbackUrl)
    {
        var method = typeof(LibGit2SharpRepositoryManager).GetMethod(
            "CredentialsHandlerFor",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var handler = (Delegate)method.Invoke(null, [authentication, expectedTransportAuthority])!;
        var supportedCredentialTypes = Enum.ToObject(handler.Method.GetParameters()[2].ParameterType, 0);
        return handler.DynamicInvoke(callbackUrl, null, supportedCredentialTypes);
    }

    private static void WithFakeCommand(string command, string script, Action action)
    {
        lock (PathLock)
        {
            var commandDirectory = TempGitFixtures.CreateTempDirectory();
            var commandPath = Path.Combine(commandDirectory, command);
            File.WriteAllText(commandPath, script);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", commandDirectory + Path.PathSeparator + originalPath);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                Directory.Delete(commandDirectory, recursive: true);
            }
        }
    }
    private string Track(string path)
    {
        cleanupPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in cleanupPaths.Distinct())
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; leftover temp directories do not affect other tests.
            }
        }
    }
}
