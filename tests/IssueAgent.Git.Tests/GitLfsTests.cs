using System.Diagnostics;
using LibGit2Sharp;

namespace IssueAgent.Git.Tests;

public sealed class GitLfsTests : IDisposable
{
    private readonly string reposRoot = TempGitFixtures.CreateTempDirectory();
    private readonly List<string> cleanupPaths = [];
    private readonly LibGit2SharpRepositoryManager manager;

    public GitLfsTests()
    {
        manager = new LibGit2SharpRepositoryManager(reposRoot);
        cleanupPaths.Add(reposRoot);
    }

    [Fact]
    public void GitLfsRunnerIsAvailableInThisEnvironment()
    {
        Assert.True(GitLfsRunner.IsAvailable());
    }

    [Fact]
    public void RepositoryRequiresLfsDetectsCommittedNestedAttributes()
    {
        var worktreePath = Track(TempGitFixtures.CreateTempDirectory());
        Directory.CreateDirectory(Path.Combine(worktreePath, "assets", "generated"));
        File.WriteAllText(
            Path.Combine(worktreePath, "assets", "generated", ".gitattributes"),
            "*.bin filter=lfs diff=lfs merge=lfs -text\n");

        Assert.True(GitLfsRunner.RepositoryRequiresLfs(worktreePath));
    }

    [Fact]
    public async Task MaterializeLfsContentAsyncNeverInstallsHooks()
    {
        var bareRemotePath = Track(CreateBareRemoteRepositoryWithLfsAsset(out var baseCommit, out _));
        await manager.EnsureBareRepositoryAsync("lfs-repo-3", bareRemotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("lfs-repo-3", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        await manager.MaterializeLfsContentAsync("lfs-repo-3", worktreePath, TempGitFixtures.AnonymousAuthentication(), _ => null, CancellationToken.None);

        var hooksDir = Path.Combine(reposRoot, "lfs-repo-3", "issueagent-disabled-hooks");
        Assert.True(Directory.Exists(hooksDir));
        Assert.Empty(Directory.GetFiles(hooksDir));
    }

    [Fact]
    public async Task MaterializeLfsContentAsyncReplacesPointerWithRealContent()
    {
        var bareRemotePath = Track(CreateBareRemoteRepositoryWithLfsAsset(out var baseCommit, out var expectedContent));
        await manager.EnsureBareRepositoryAsync("lfs-repo-1", bareRemotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("lfs-repo-1", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);

        Assert.True(manager.WorktreeRequiresLfs(worktreePath));

        var assetPath = Path.Combine(worktreePath, "asset.bin");
        var pointerContent = File.ReadAllText(assetPath);
        Assert.Contains("git-lfs", pointerContent, StringComparison.Ordinal);

        await manager.MaterializeLfsContentAsync("lfs-repo-1", worktreePath, TempGitFixtures.AnonymousAuthentication(), _ => null, CancellationToken.None);

        var materializedBytes = File.ReadAllBytes(assetPath);
        Assert.Equal(expectedContent, materializedBytes);
    }

    [Fact]
    public async Task MaterializeLfsContentAsyncRecursivelyMaterializesInitializedSubmoduleContent()
    {
        var submoduleRemotePath = Track(CreateBareRemoteRepositoryWithLfsAsset(out _, out var expectedContent));
        var parentRepositoryPath = Track(TempGitFixtures.CreateTempDirectory());
        Repository.Init(parentRepositoryPath);
        RunGitCli(parentRepositoryPath, "-c", "protocol.file.allow=always", "submodule", "add", submoduleRemotePath, "dependencies/assets");
        RunGitCli(parentRepositoryPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-am", "Add LFS submodule");

        var baseCommit = new Repository(parentRepositoryPath).Head.Tip.Sha;
        await manager.EnsureBareRepositoryAsync("lfs-submodule-repo", parentRepositoryPath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("lfs-submodule-repo", "wt-submodule", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        await manager.UpdateSubmodulesAsync("lfs-submodule-repo", worktreePath, _ => null, CancellationToken.None);

        var assetPath = Path.Combine(worktreePath, "dependencies", "assets", "asset.bin");
        Assert.Contains("git-lfs", File.ReadAllText(assetPath), StringComparison.Ordinal);

        await manager.MaterializeLfsContentAsync("lfs-submodule-repo", worktreePath, TempGitFixtures.AnonymousAuthentication(), _ => null, CancellationToken.None);

        Assert.Equal(expectedContent, File.ReadAllBytes(assetPath));
    }

    [Fact]
    public async Task UploadLfsObjectsAsyncPublishesObjectsBeforeRefPush()
    {
        var bareRemotePath = Track(CreateBareRemoteRepositoryWithLfsAsset(out var baseCommit, out var expectedContent));
        await manager.EnsureBareRepositoryAsync("lfs-repo-2", bareRemotePath, TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        var worktreePath = Track(Path.Combine(TempGitFixtures.CreateTempDirectory(), "wt"));
        await manager.CreateWorktreeAsync("lfs-repo-2", "wt-1", worktreePath, "agent/issue-1", baseCommit, CancellationToken.None);
        await manager.MaterializeLfsContentAsync("lfs-repo-2", worktreePath, TempGitFixtures.AnonymousAuthentication(), _ => null, CancellationToken.None);

        var newAssetBytes = new byte[2048];
        Random.Shared.NextBytes(newAssetBytes);
        var newAssetPath = Path.Combine(worktreePath, "new-asset.bin");
        File.WriteAllBytes(newAssetPath, newAssetBytes);

        RunGitCli(worktreePath, "add", "new-asset.bin");
        RunGitCli(worktreePath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Add new LFS asset");
        var commitSha = new Repository(worktreePath).Head.Tip.Sha;

        await manager.UploadLfsObjectsAsync("lfs-repo-2", worktreePath, "agent/issue-1", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);
        await manager.PushAsync("lfs-repo-2", worktreePath, "agent/issue-1", TempGitFixtures.AnonymousAuthentication(), CancellationToken.None);

        using var bareRepo = new Repository(bareRemotePath);
        var remoteBranch = bareRepo.Branches["agent/issue-1"];
        Assert.NotNull(remoteBranch);
        Assert.Equal(commitSha, remoteBranch.Tip.Sha);

        // Materialize from a fresh clone to prove the LFS object actually reached the remote store.
        var verificationClonePath = Track(TempGitFixtures.CreateTempDirectory());
        Directory.Delete(verificationClonePath);
        RunGitCli(TempGitFixtures.CreateTempDirectory(), "clone", "--branch", "agent/issue-1", bareRemotePath, verificationClonePath);
        RunGitCli(verificationClonePath, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
        RunGitCli(verificationClonePath, "config", "filter.lfs.process", "git-lfs filter-process");
        RunGitCli(verificationClonePath, "config", "filter.lfs.required", "true");
        RunGitCli(verificationClonePath, "lfs", "pull");

        var verifiedBytes = File.ReadAllBytes(Path.Combine(verificationClonePath, "new-asset.bin"));
        Assert.Equal(newAssetBytes, verifiedBytes);
    }

    private static string CreateBareRemoteRepositoryWithLfsAsset(out string firstCommitSha, out byte[] assetContent)
    {
        var workingPath = TempGitFixtures.CreateTempDirectory();
        Repository.Init(workingPath);

        RunGitCli(workingPath, "lfs", "install", "--local");
        File.WriteAllText(Path.Combine(workingPath, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");

        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(Path.Combine(workingPath, "asset.bin"), content);

        RunGitCli(workingPath, "add", ".gitattributes", "asset.bin");
        RunGitCli(workingPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Add LFS asset");

        using (var repo = new Repository(workingPath))
        {
            firstCommitSha = repo.Head.Tip.Sha;
            if (repo.Head.FriendlyName != "main")
            {
                repo.Refs.Rename(repo.Head.Reference, "refs/heads/main");
            }
        }

        var barePath = TempGitFixtures.CreateTempDirectory();
        Directory.Delete(barePath);
        Repository.Clone(workingPath, barePath, new CloneOptions { IsBare = true });
        CopyDirectory(Path.Combine(workingPath, ".git", "lfs"), Path.Combine(barePath, "lfs"));
        assetContent = content;
        return barePath;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        Directory.CreateDirectory(destinationDir);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            var destinationFile = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
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
                // Best-effort cleanup.
            }
        }
    }
}
