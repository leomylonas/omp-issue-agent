using Xunit;
using IssueAgent.Git;
using LibGit2Sharp;

namespace IssueAgent.Security.Tests;

/// <summary>Proves hooks never execute on the real CLI subprocess paths (<see cref="GitLfsRunner"/>)
/// by installing a real executable hook and running a real <c>git</c>/<c>git-lfs</c> process against
/// it, rather than asserting on LibGit2Sharp (which never runs hooks regardless). Covers
/// specification §11, §36 "real hook-execution test".</summary>
public sealed class HookNonExecutionTests
{
    [Fact]
    public async Task UploadObjectsAsyncNeverExecutesAPrePushHook()
    {
        if (!GitLfsRunner.IsAvailable())
        {
            Assert.Skip("git-lfs is not available in this environment.");
        }

        var barePath = CreateTempDirectory();
        Repository.Init(barePath, isBare: true);

        var worktreePath = CreateTempDirectory();
        Repository.Init(worktreePath);
        using (var repo = new Repository(worktreePath))
        {
            File.WriteAllText(Path.Combine(worktreePath, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
            File.WriteAllBytes(Path.Combine(worktreePath, "payload.bin"), [1, 2, 3, 4, 5]);
            Commands.Stage(repo, "*");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("Add LFS payload", signature, signature);
            if (repo.Head.FriendlyName != "main")
            {
                repo.Refs.Rename(repo.Head.Reference, "refs/heads/main");
            }

            repo.Network.Remotes.Add("origin", barePath);
        }

        var sentinelPath = Path.Combine(worktreePath, "hook-executed.sentinel");
        InstallExecutableHook(worktreePath, "pre-push", sentinelPath);
        InstallExecutableHook(worktreePath, "post-checkout", sentinelPath);

        var authentication = GitAuthentication.Anonymous(TlsTrust.System);

        try
        {
            await GitLfsRunner.UploadObjectsAsync(worktreePath, barePath, "main", authentication, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // A local-path LFS remote may still fail on some environments; the assertion below is
            // what actually matters — the hook must never fire regardless of the push outcome.
        }

        Assert.False(File.Exists(sentinelPath), "A Git hook executed during a CLI subprocess operation that must run with hooks disabled.");
    }

    private static void InstallExecutableHook(string worktreePath, string hookName, string sentinelPath)
    {
        var hooksDir = Path.Combine(worktreePath, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, hookName);
        File.WriteAllText(hookPath, $"#!/bin/sh\ntouch \"{sentinelPath}\"\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "issueagent-security-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
