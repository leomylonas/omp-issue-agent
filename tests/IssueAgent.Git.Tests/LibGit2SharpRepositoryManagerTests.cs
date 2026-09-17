using System.Diagnostics;
using LibGit2Sharp;

namespace IssueAgent.Git.Tests;

public sealed class LibGit2SharpRepositoryManagerTests : IDisposable
{
    private readonly string reposRoot = TempGitFixtures.CreateTempDirectory();
    private readonly List<string> cleanupPaths = [];
    private readonly LibGit2SharpRepositoryManager manager;

    public LibGit2SharpRepositoryManagerTests()
    {
        manager = new LibGit2SharpRepositoryManager(reposRoot);
        cleanupPaths.Add(reposRoot);
    }

    private static void RunGitCli(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }
    }

    [Fact]
    public async Task EnsureBareRepositoryAsyncClonesLazilyAndIsIdempotent()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out _));

        await manager.EnsureBareRepositoryAsync("repo-1", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        await manager.EnsureBareRepositoryAsync("repo-1", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        Assert.True(Repository.IsValid(Path.Combine(reposRoot, "repo-1")));
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
    public async Task CreateWorktreeAsyncChecksOutNewBranchAtBaseCommit()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-4", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));

        await manager.CreateWorktreeAsync("repo-4", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(worktreePath, "README.md")));
        Assert.Equal(baseCommit, await manager.GetHeadCommitAsync(worktreePath, CancellationToken.None));
        Assert.False(await manager.HasUncommittedChangesAsync(worktreePath, CancellationToken.None));
    }

    [Fact]
    public async Task ResetWorktreeAsyncDiscardsUncommittedChanges()
    {
        var remotePath = Track(TempGitFixtures.CreateRemoteRepositoryWithCommit(out var baseCommit));
        await manager.EnsureBareRepositoryAsync("repo-5", remotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("repo-5", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        File.WriteAllText(Path.Combine(worktreePath, "scratch.txt"), "dirty");
        Assert.True(await manager.HasUncommittedChangesAsync(worktreePath, CancellationToken.None));

        await manager.ResetWorktreeAsync(worktreePath, baseCommit, CancellationToken.None);

        Assert.False(await manager.HasUncommittedChangesAsync(worktreePath, CancellationToken.None));
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
        var merged = await manager.TryMergeAsync(worktreePath, nonConflictingCommit, TempGitFixtures.TestIdentity, CancellationToken.None);

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
        var merged = await manager.TryMergeAsync(worktreePath, conflictingCommit, TempGitFixtures.TestIdentity, CancellationToken.None);

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
        var rebased = await manager.TryRebaseOntoAsync(worktreePath, newBaseCommit, TempGitFixtures.TestIdentity, CancellationToken.None);

        Assert.True(rebased);
        Assert.True(File.Exists(Path.Combine(worktreePath, "unrelated.txt")));
        Assert.True(File.Exists(Path.Combine(worktreePath, "agent-work.txt")));
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

        await manager.PushAsync(worktreePath, "agent/issue-1", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        using var bareRepo = new Repository(bareRemotePath);
        var remoteBranch = bareRepo.Branches["agent/issue-1"];
        Assert.NotNull(remoteBranch);
        Assert.Equal(commitSha, remoteBranch.Tip.Sha);
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

        await manager.UpdateSubmodulesAsync(worktreePath, _ => null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(worktreePath, "lib", "dependency", "README.md")));
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
