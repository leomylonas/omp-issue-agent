using System.Diagnostics;
using System.Reflection;
using LibGit2Sharp;

namespace IssueAgent.Git.Tests;

/// <summary>Proves the LFS token is forwarded only to the trusted origin host (specification §11),
/// never to a host named by a committed <c>.lfsconfig</c> override.</summary>
public sealed class GitLfsCredentialScopingTests : IDisposable
{
    private readonly List<string> cleanupPaths = [];

    [Fact]
    public void ApplyAuthenticationNeverForwardsTokenWhenLfsConfigNamesADifferentHost()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        File.WriteAllText(Path.Combine(worktreePath, ".lfsconfig"), "[lfs]\n\turl = https://attacker.example/lfs\n");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track());

        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey("ISSUEAGENT_GIT_TOKEN"));
    }

    [Fact]
    public void ApplyAuthenticationForwardsTokenWhenNoLfsConfigOverridesTheOriginHost()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track());

        Assert.True(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.Equal("super-secret-token", startInfo.Environment["ISSUEAGENT_GIT_TOKEN"]);
    }

    [Fact]
    public void ApplyAuthenticationForwardsTokenWhenLfsConfigMatchesOriginHost()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        File.WriteAllText(Path.Combine(worktreePath, ".lfsconfig"), "[lfs]\n\turl = https://git.trusted.example/octo/widgets.git/info/lfs\n");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track());

        Assert.True(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
    }

    [Fact]
    public void ApplyAuthenticationNeverForwardsTokenWhenLfsEndpointChangesTheOriginPort()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example:8443/octo/widgets.git");
        File.WriteAllText(Path.Combine(worktreePath, ".lfsconfig"), "[lfs]\n\turl = https://git.trusted.example/octo/widgets.git/info/lfs\n");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track());

        Assert.False(startInfo.Environment.ContainsKey("ISSUEAGENT_GIT_TOKEN"));
    }

    [Fact]
    public void ApplyAuthenticationNeverForwardsTokenToAnHttpLfsEndpointOnTheOriginHost()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        File.WriteAllText(Path.Combine(worktreePath, ".lfsconfig"), "[lfs]\n\turl = http://git.trusted.example/octo/widgets.git/info/lfs\n");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track()));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.False(startInfo.Environment.ContainsKey("ISSUEAGENT_GIT_TOKEN"));
    }

    [Theory]
    [InlineData("lfs.pushurl", "https://attacker.example/lfs")]
    [InlineData("remote.origin.lfsurl", "https://attacker.example/lfs")]
    [InlineData("remote.origin.lfspushurl", "https://attacker.example/lfs")]
    [InlineData("remote.origin.pushurl", "https://attacker.example/widgets.git")]
    public void ApplyAuthenticationNeverForwardsTokenWhenAnyLfsPullOrPushOverrideNamesADifferentAuthority(string key, string endpoint)
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        RunGitCli(worktreePath, "config", key, endpoint);
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track());

        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey("ISSUEAGENT_GIT_TOKEN"));
    }

    [Fact]
    public void ApplyAuthenticationConfiguresAdditionalCaForHttpsLfs()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var certificatePath = Path.Combine(Track(), "additional-ca.pem");
        File.WriteAllText(certificatePath, "test certificate");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication
        {
            Mode = GitAuthenticationMode.Token,
            HttpsToken = "super-secret-token",
            TlsTrust = new TlsTrust
            {
                Mode = TlsTrustMode.SystemPlusAdditionalCa,
                AdditionalCaCertificatePaths = [certificatePath],
            },
        }, Track());

        Assert.Equal(certificatePath, startInfo.Environment["GIT_SSL_CAINFO"]);
        Assert.Equal("super-secret-token", startInfo.Environment["ISSUEAGENT_GIT_TOKEN"]);
    }

    [Fact]
    public void ApplyAuthenticationConfiguresAdditionalCaForSshAuthenticatedHttpsLfs()
    {
        var worktreePath = CreateWorktreeWithOrigin("ssh://git.trusted.example/octo/widgets.git");
        var certificatePath = Path.Combine(Track(), "additional-ca.pem");
        File.WriteAllText(certificatePath, "test certificate");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication
        {
            Mode = GitAuthenticationMode.Ssh,
            SshPrivateKey = "test key",
            SshTrust = new SshTrust { Mode = SshHostVerificationMode.None },
            TlsTrust = new TlsTrust
            {
                Mode = TlsTrustMode.SystemPlusAdditionalCa,
                AdditionalCaCertificatePaths = [certificatePath],
            },
        }, Track());

        Assert.Equal(certificatePath, startInfo.Environment["GIT_SSL_CAINFO"]);
        Assert.True(startInfo.Environment.ContainsKey("GIT_SSH_COMMAND"));
    }

    [Fact]
    public void ApplyAuthenticationFailsClosedForSshAuthenticatedHttpsLfsWithPinnedTls()
    {
        var worktreePath = CreateWorktreeWithOrigin("ssh://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeApplyAuthentication(startInfo, new GitAuthentication
            {
                Mode = GitAuthenticationMode.Ssh,
                SshPrivateKey = "test key",
                SshTrust = new SshTrust { Mode = SshHostVerificationMode.None },
                TlsTrust = new TlsTrust { Mode = TlsTrustMode.Pinned, Fingerprints = ["sha256/fingerprint"] },
            }, Track()));

        var failure = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("system-plus-additional-ca", failure.Message, StringComparison.Ordinal);
        Assert.False(startInfo.Environment.ContainsKey("GIT_SSH_COMMAND"));
    }

    [Fact]
    public void ApplyAuthenticationNeverForwardsTokenWhenOriginUsesHttp()
    {
        var worktreePath = CreateWorktreeWithOrigin("http://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track());

        Assert.False(startInfo.Environment.ContainsKey("ISSUEAGENT_GIT_TOKEN"));
    }

    [Fact]
    public void ApplyAuthenticationFailsClosedWhenGitLfsCannotEnforceConfiguredTlsTrust()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeApplyAuthentication(startInfo, new GitAuthentication
            {
                Mode = GitAuthenticationMode.Token,
                HttpsToken = "super-secret-token",
                TlsTrust = new TlsTrust { Mode = TlsTrustMode.Pinned, Fingerprints = ["sha256/fingerprint"] },
            }, Track()));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.False(startInfo.Environment.ContainsKey("ISSUEAGENT_GIT_TOKEN"));
    }

    private string CreateWorktreeWithOrigin(string originUrl)
    {
        var worktreePath = Track();
        Repository.Init(worktreePath);
        RunGitCli(worktreePath, "remote", "add", "origin", originUrl);
        return worktreePath;
    }

    private static void InvokeApplyAuthentication(ProcessStartInfo startInfo, GitAuthentication authentication, string isolatedHome)
    {
        var method = typeof(GitLfsRunner).GetMethod("ApplyAuthentication", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [startInfo, authentication, isolatedHome]);
    }

    private static void RunGitCli(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit();
    }

    private string Track()
    {
        var path = TempGitFixtures.CreateTempDirectory();
        cleanupPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in cleanupPaths)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
