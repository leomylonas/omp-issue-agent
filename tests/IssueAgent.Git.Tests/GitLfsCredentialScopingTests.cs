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
    public void ApplyAuthenticationUsesTokenForTrustedCanonicalRemoteDespiteMutableLfsConfig()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        File.WriteAllText(Path.Combine(worktreePath, ".lfsconfig"), "[lfs]\n\turl = https://attacker.example/lfs\n");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(startInfo, new GitAuthentication { Mode = GitAuthenticationMode.Token, HttpsToken = "super-secret-token" }, Track());

        Assert.True(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.Equal("super-secret-token", startInfo.Environment["ISSUEAGENT_GIT_TOKEN"]);
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
    public void TrustedLfsConfigurationOverridesMutableWorktreeEndpoints()
    {
        var startInfo = new ProcessStartInfo();

        InvokeAddTrustedLfsEndpointConfiguration(
            startInfo,
            "https://git.trusted.example/octo/widgets.git");

        Assert.Equal(
            ["-c", "credential.helper=", "-c", "lfs.url=https://git.trusted.example/octo/widgets.git/info/lfs",
             "-c", "lfs.pushurl=https://git.trusted.example/octo/widgets.git/info/lfs"],
            startInfo.ArgumentList);
    }


    [Fact]
    public void IsolatedLfsEnvironmentClearsInheritedGitConfigurationAndCredentialPrograms()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "credential.helper";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = "!attacker";
        startInfo.Environment["GIT_ASKPASS"] = "/tmp/attacker-askpass";
        startInfo.Environment["SSH_ASKPASS"] = "/tmp/attacker-ssh-askpass";
        startInfo.Environment["GIT_SSH_COMMAND"] = "ssh attacker";
        startInfo.Environment["GIT_DIR"] = "/tmp/attacker-git-dir";

        InvokeConfigureIsolatedEnvironment(startInfo, Track());

        Assert.DoesNotContain(startInfo.Environment.Keys, key =>
            key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) &&
            !key.Equals("GIT_CONFIG_NOSYSTEM", StringComparison.OrdinalIgnoreCase) &&
            !key.Equals("GIT_CONFIG_GLOBAL", StringComparison.OrdinalIgnoreCase));
        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey("SSH_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey("GIT_SSH_COMMAND"));
        Assert.False(startInfo.Environment.ContainsKey("GIT_DIR"));
        Assert.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", startInfo.Environment["GIT_CONFIG_GLOBAL"]);
    }

    [Fact]
    public void TrustedLfsConfigurationPinsSshEndpointToCanonicalRemote()
    {
        var startInfo = new ProcessStartInfo();

        InvokeAddTrustedLfsEndpointConfiguration(
            startInfo,
            "ssh://remote-user@git.trusted.example/octo/widgets.git");

        Assert.Equal(
            ["-c", "credential.helper=", "-c", "lfs.url=ssh://remote-user@git.trusted.example/octo/widgets.git",
             "-c", "lfs.pushurl=ssh://remote-user@git.trusted.example/octo/widgets.git"],
            startInfo.ArgumentList);
    }

    [Fact]
    public void TrustedLfsConfigurationNormalizesScpLikeSshEndpoint()
    {
        var startInfo = new ProcessStartInfo();

        InvokeAddTrustedLfsEndpointConfiguration(
            startInfo,
            "remote-user@git.trusted.example:octo/widgets.git");

        Assert.Equal(
            ["-c", "credential.helper=", "-c", "lfs.url=ssh://remote-user@git.trusted.example/octo/widgets.git",
             "-c", "lfs.pushurl=ssh://remote-user@git.trusted.example/octo/widgets.git"],
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("ssh://remote-user@git.trusted.example:2222/octo/widgets.git", "https://git.trusted.example/octo/widgets.git/info/lfs")]
    [InlineData("remote-user@git.trusted.example:octo/widgets.git", "https://git.trusted.example/octo/widgets.git/info/lfs")]
    public void SshRemoteResolvesToActualHttpsLfsEndpointForTlsPinning(string remote, string expectedEndpoint)
    {
        var resolveEndpoint = typeof(GitLfsRunner).GetMethod(
            "TryGetHttpsLfsEndpoint",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var arguments = new object?[] { remote, null };

        Assert.True((bool)resolveEndpoint.Invoke(null, arguments)!);
        Assert.Equal(expectedEndpoint, Assert.IsType<Uri>(arguments[1]).AbsoluteUri);
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
    public void ApplyAuthenticationAppliesAdditionalCaTrustToAnonymousHttpsLfs()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var certificatePath = WriteAdditionalCertificate(X509ContentType.Cert, "anonymous-additional-ca.pem");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(
            startInfo,
            GitAuthentication.Anonymous(new TlsTrust
            {
                Mode = TlsTrustMode.SystemPlusAdditionalCa,
                AdditionalCaCertificatePaths = [certificatePath],
            }),
            Track());

        var bundlePath = Assert.IsType<string>(startInfo.Environment["GIT_SSL_CAINFO"]);
        Assert.NotEqual(certificatePath, bundlePath);
        Assert.Contains(File.ReadAllText(certificatePath), File.ReadAllText(bundlePath), StringComparison.Ordinal);
        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
    }

    [Fact]
    public void ApplyAuthenticationAppliesNoneTrustToAnonymousHttpsLfs()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        InvokeApplyAuthentication(
            startInfo,
            GitAuthentication.Anonymous(new TlsTrust { Mode = TlsTrustMode.None }),
            Track());

        Assert.Equal("true", startInfo.Environment["GIT_SSL_NO_VERIFY"]);
    }

    [Fact]
    public void ApplyAuthenticationRemovesInheritedTlsOverridesForSystemTrust()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };
        startInfo.Environment["GIT_SSL_NO_VERIFY"] = "true";
        startInfo.Environment["GIT_SSL_CAINFO"] = "/tmp/attacker-ca.pem";
        startInfo.Environment["GIT_SSL_CIPHER_LIST"] = "insecure";

        InvokeApplyAuthentication(startInfo, GitAuthentication.Anonymous(TlsTrust.System), Track());

        Assert.DoesNotContain(startInfo.Environment.Keys, key => key.StartsWith("GIT_SSL_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyAuthenticationReplacesInheritedCaOverrideForNoneTrust()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };
        startInfo.Environment["GIT_SSL_CAINFO"] = "/tmp/attacker-ca.pem";

        InvokeApplyAuthentication(
            startInfo,
            GitAuthentication.Anonymous(new TlsTrust { Mode = TlsTrustMode.None }),
            Track());

        Assert.Equal("true", startInfo.Environment["GIT_SSL_NO_VERIFY"]);
        Assert.False(startInfo.Environment.ContainsKey("GIT_SSL_CAINFO"));
    }

    [Fact]
    public void ApplyAuthenticationRejectsPinnedHttpsLfsWithoutAFingerprint()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            InvokeApplyAuthentication(
                startInfo,
                GitAuthentication.Anonymous(new TlsTrust
                {
                    Mode = TlsTrustMode.Pinned,
                }),
                Track()));

        Assert.Contains("requires at least one certificate fingerprint", failure.Message, StringComparison.Ordinal);
        Assert.False(startInfo.Environment.ContainsKey("GIT_SSL_NO_VERIFY"));
        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
    }

    [Fact]
    public async Task PinnedTlsBuildsLfsBundleFromOnlyThePinnedEndpointCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        var cancellationToken = TestContext.Current.CancellationToken;
        listener.Start();
        var accept = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = new System.Net.Security.SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await stream.AuthenticateAsServerAsync(
                new System.Net.Security.SslServerAuthenticationOptions { ServerCertificate = certificate },
                cancellationToken);
        }, cancellationToken);
        var isolatedHome = Track();
        var createBundle = typeof(GitLfsRunner).GetMethod(
            "CreatePinnedCertificateBundleAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        var bundle = await (Task<string>)createBundle.Invoke(null,
        [
            new Uri($"https://localhost:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/"),
            new[] { fingerprint },
            isolatedHome,
            cancellationToken,
        ])!;
        await accept;

        Assert.Equal(certificate.ExportCertificatePem(), File.ReadAllText(bundle));
    }

    [Fact]
    public void ApplyAuthenticationPreservesEveryCertificateInAdditionalPemChain()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var certificatePath = WriteAdditionalCertificate(X509ContentType.Cert, "additional-chain.pem");
        var secondCertificatePath = WriteAdditionalCertificate(X509ContentType.Cert, "additional-intermediate.pem");
        var secondCertificate = File.ReadAllText(secondCertificatePath);
        File.AppendAllText(certificatePath, secondCertificate);
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
        Assert.Contains(File.ReadAllText(certificatePath), bundle, StringComparison.Ordinal);
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
    public void ApplyAuthenticationRejectsPinnedSshLfsWithoutAFingerprint()
    {
        var worktreePath = CreateWorktreeWithOrigin("ssh://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            InvokeApplyAuthentication(startInfo, new GitAuthentication
            {
                Mode = GitAuthenticationMode.Ssh,
                SshPrivateKey = "test key",
                SshTrust = new SshTrust { Mode = SshHostVerificationMode.None },
                TlsTrust = new TlsTrust { Mode = TlsTrustMode.Pinned },
            }, Track()));

        Assert.Contains("requires at least one certificate fingerprint", failure.Message, StringComparison.Ordinal);
        Assert.False(startInfo.Environment.ContainsKey("GIT_SSH_COMMAND"));
    }

    [Fact]
    public void SshPassphraseAskPassIsScopedToTheChildEnvironment()
    {
        var startInfo = new ProcessStartInfo();
        var isolatedHome = Track();
        var configureAskPass = typeof(GitSshTransport).GetMethod(
            "ConfigurePassphraseAskPass",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        configureAskPass.Invoke(null,
        [
            startInfo,
            new GitAuthentication
            {
                Mode = GitAuthenticationMode.Ssh,
                SshPrivateKeyPassphrase = "correct horse battery staple",
            },
            isolatedHome,
        ]);
        Assert.Equal("force", startInfo.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("correct horse battery staple", RunSshAskPass(startInfo));
    }

    [Fact]
    public void AskPassReturnsUsernameOnlyForUsernamePrompts()
    {
        var worktreePath = CreateWorktreeWithOrigin("https://git.trusted.example/octo/widgets.git");
        var startInfo = new ProcessStartInfo { WorkingDirectory = worktreePath };
        var isolatedHome = Track();

        InvokeApplyAuthentication(
            startInfo,
            new GitAuthentication
            {
                Mode = GitAuthenticationMode.Token,
                HttpsUsername = "issue-agent",
                HttpsToken = "super-secret-token",
            },
            isolatedHome);

        Assert.Equal("issue-agent", RunAskPass(startInfo, "Username for 'https://git.trusted.example':"));
        Assert.Equal("super-secret-token", RunAskPass(startInfo, "Password for 'https://git.trusted.example':"));
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
        var method = typeof(GitLfsRunner).GetMethod("ApplyAuthenticationAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((Task)method.Invoke(null, [startInfo, canonicalRemoteUrl, authentication, isolatedHome, CancellationToken.None])!).GetAwaiter().GetResult();
    }

    private static void InvokeAddTrustedLfsEndpointConfiguration(
        ProcessStartInfo startInfo,
        string canonicalRemoteUrl)
    {
        var method = typeof(GitLfsRunner).GetMethod(
            "AddTrustedLfsEndpointConfiguration",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [startInfo, canonicalRemoteUrl]);
    }

    private static void InvokeConfigureIsolatedEnvironment(ProcessStartInfo startInfo, string isolatedHome)
    {
        var method = typeof(GitLfsRunner).GetMethod(
            "ConfigureIsolatedEnvironment",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [startInfo, isolatedHome]);
    }

    private static string RunAskPass(ProcessStartInfo authenticationStartInfo, string prompt)
    {
        var startInfo = new ProcessStartInfo(authenticationStartInfo.Environment["GIT_ASKPASS"]!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(prompt);
        foreach (var variable in authenticationStartInfo.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = Process.Start(startInfo)!;
        var result = process.StandardOutput.ReadToEnd().TrimEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return result;
    }

    private static string RunSshAskPass(ProcessStartInfo authenticationStartInfo)
    {
        var startInfo = new ProcessStartInfo(authenticationStartInfo.Environment["SSH_ASKPASS"]!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var variable in authenticationStartInfo.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = Process.Start(startInfo)!;
        var result = process.StandardOutput.ReadToEnd().TrimEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return result;
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
