using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public void ApplyAuthenticationNeverForwardsTokenWhenWorktreeOriginDiffersFromProtectedCanonicalRemote()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://attacker.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(
            startInfo,
            "https://git.trusted.example/octo/widgets.git",
            new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" },
            Track());

        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey("ISSUEAGENT_GIT_TOKEN"));
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
    public void ApplyAuthenticationBundlesSystemAndPemAdditionalCaForHttpsLfs()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var certificatePath = WriteAdditionalCertificate(X509ContentType.Cert, "additional-ca.pem");
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

        var bundlePath = Assert.IsType<string>(startInfo.Environment["GIT_SSL_CAINFO"]);
        Assert.NotEqual(certificatePath, bundlePath);
        var bundle = File.ReadAllText(bundlePath);
        Assert.Contains(File.ReadAllText("/etc/ssl/certs/ca-certificates.crt"), bundle, StringComparison.Ordinal);
        Assert.Contains(File.ReadAllText(certificatePath), bundle, StringComparison.Ordinal);
        Assert.Equal("super-secret-token", startInfo.Environment["ISSUEAGENT_GIT_TOKEN"]);
    }

    [Fact]
    public void ApplyAuthenticationNormalizesDerAdditionalCaToPemForHttpsLfs()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var certificatePath = WriteAdditionalCertificate(X509ContentType.Cert, "additional-ca.der", pem: false);
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

        var bundle = File.ReadAllText(Assert.IsType<string>(startInfo.Environment["GIT_SSL_CAINFO"]));
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        Assert.Contains(certificate.ExportCertificatePem(), bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAuthenticationBundlesSystemAndAdditionalCaForSshAuthenticatedHttpsLfs()
    {
        var worktreePath = CreateWorktreeWithOrigin("ssh://git.trusted.example/octo/widgets.git");
        var certificatePath = WriteAdditionalCertificate(X509ContentType.Cert, "additional-ca.pem");
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

        Assert.NotEqual(certificatePath, startInfo.Environment["GIT_SSL_CAINFO"]);
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

    private static void InvokeApplyAuthentication(
        ProcessStartInfo startInfo,
        GitAuthentication authentication,
        string isolatedHome) =>
        InvokeApplyAuthentication(startInfo, "https://git.trusted.example/octo/widgets.git", authentication, isolatedHome);

    private static void InvokeApplyAuthentication(
        ProcessStartInfo startInfo,
        string canonicalRemoteUrl,
        GitAuthentication authentication,
        string isolatedHome)
    {
        var method = typeof(GitLfsRunner).GetMethod("ApplyAuthentication", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [startInfo, canonicalRemoteUrl, authentication, isolatedHome]);
    }

    private string WriteAdditionalCertificate(X509ContentType contentType, string fileName, bool pem = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=additional-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Track(), fileName);
        File.WriteAllBytes(path, certificate.Export(contentType));
        if (pem)
        {
            File.WriteAllText(path, certificate.ExportCertificatePem());
        }
        return path;
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
