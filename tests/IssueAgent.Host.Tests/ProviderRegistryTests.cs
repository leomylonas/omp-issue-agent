using IssueAgent.Configuration;
using IssueAgent.Git;
using IssueAgent.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void SubmoduleAuthenticationRetainsEquivalentHostMappings()
    {
        var registry = CreateRegistry(
            Repository("repository-a", "https://git.example.test/team/a.git", "shared-token"),
            Repository("repository-b", "https://git.example.test/team/b.git", "shared-token"),
            Repository("repository-local", "/srv/git/local.git", "token-local"));

        var authentication = registry.GetSubmoduleGitAuthentication("repository-local", "git.example.test");

        Assert.NotNull(authentication);
        Assert.Equal("shared-token", authentication.HttpsToken);
        Assert.Equal(TlsTrustMode.System, authentication.TlsTrust.Mode);
    }

    [Fact]
    public void SubmoduleAuthenticationUsesCurrentRepositoryAndRejectsDifferentHostMappings()
    {
        var registry = CreateRegistry(
            Repository("repository-a", "https://git.example.test/team/a.git", "token-a"),
            Repository("repository-b", "https://git.example.test/team/b.git", "token-b"),
            Repository("repository-c", "https://other.example.test/team/c.git", "token-c"),
            Repository("repository-local", "/srv/git/local.git", "token-local"));

        Assert.Equal("token-a", registry.GetSubmoduleGitAuthentication("repository-a", "git.example.test")?.HttpsToken);
        Assert.Equal("token-b", registry.GetSubmoduleGitAuthentication("repository-b", "git.example.test")?.HttpsToken);
        Assert.Null(registry.GetSubmoduleGitAuthentication("repository-local", "git.example.test"));
        Assert.Equal("token-c", registry.GetSubmoduleGitAuthentication("repository-a", "other.example.test")?.HttpsToken);
    }

    [Fact]
    public void SubmoduleAuthenticationTreatsTrustCollectionsAsSetsAndRejectsDifferentValues()
    {
        var equivalent = CreateRegistry(
            Repository("repository-a", "https://git.example.test/team/a.git", "shared-token",
                ["ca-a.pem", "ca-b.pem"], ["sha256:tls-a", "sha256:tls-b"], ["sha256:ssh-a", "sha256:ssh-b"]),
            Repository("repository-b", "https://git.example.test/team/b.git", "shared-token",
                ["ca-b.pem", "ca-a.pem"], ["sha256:tls-a", "sha256:tls-b"], ["SHA256:SSH-B", "SHA256:SSH-A"]),
            Repository("repository-local", "/srv/git/local.git", "token-local",
                ["ca-a.pem", "ca-b.pem"], ["sha256:tls-a", "sha256:tls-b"]));

        Assert.Equal("shared-token", equivalent.GetSubmoduleGitAuthentication("repository-local", "git.example.test")?.HttpsToken);

        var differentSshFingerprint = CreateRegistry(
            Repository("repository-a", "https://git.example.test/team/a.git", "shared-token",
                ["ca-a.pem", "ca-b.pem"], ["sha256:tls-a", "sha256:tls-b"], ["sha256:ssh-a", "sha256:ssh-b"]),
            Repository("repository-b", "https://git.example.test/team/b.git", "shared-token",
                ["ca-a.pem", "ca-b.pem"], ["sha256:tls-a", "sha256:tls-b"], ["sha256:ssh-a", "sha256:ssh-c"]),
            Repository("repository-local", "/srv/git/local.git", "token-local",
                ["ca-a.pem", "ca-b.pem"], ["sha256:tls-a", "sha256:tls-b"]));
        Assert.Null(differentSshFingerprint.GetSubmoduleGitAuthentication("repository-local", "git.example.test"));
        Assert.Throws<InvalidOperationException>(() => CreateRegistry(
            Repository("repository-a", "https://git.example.test/team/a.git", "shared-token",
                ["ca-a.pem", "ca-b.pem"], ["sha256:tls-a", "sha256:tls-b"], ["sha256:ssh-a", "sha256:ssh-b"]),
            Repository("repository-b", "https://git.example.test/team/b.git", "shared-token",
                ["ca-a.pem", "ca-c.pem"], ["sha256:tls-a", "sha256:tls-b"], ["sha256:ssh-a", "sha256:ssh-b"]),
            Repository("repository-local", "/srv/git/local.git", "token-local",
                ["ca-a.pem", "ca-b.pem"], ["sha256:tls-a", "sha256:tls-b"])));
    }

    [Fact]
    public void ProviderTlsTrustTreatsPinnedFingerprintCollectionsAsSets()
    {
        var equivalent = CreateRegistry(
            Repository("repository-a", "https://git.example.test/team/a.git", "shared-token",
                tlsFingerprints: ["sha256:tls-a", "sha256:tls-b"], tlsMode: ConfiguredTlsTrustMode.Pinned),
            Repository("repository-b", "https://git.example.test/team/b.git", "shared-token",
                tlsFingerprints: ["sha256:tls-b", "sha256:tls-a"], tlsMode: ConfiguredTlsTrustMode.Pinned));

        Assert.NotNull(equivalent.Get("github"));

        Assert.Throws<InvalidOperationException>(() => CreateRegistry(
            Repository("repository-a", "https://git.example.test/team/a.git", "shared-token",
                tlsFingerprints: ["sha256:tls-a", "sha256:tls-b"], tlsMode: ConfiguredTlsTrustMode.Pinned),
            Repository("repository-b", "https://git.example.test/team/b.git", "shared-token",
                tlsFingerprints: ["sha256:tls-a", "sha256:tls-c"], tlsMode: ConfiguredTlsTrustMode.Pinned)));
    }

    private static ProviderRegistry CreateRegistry(params EffectiveRepositoryConfiguration[] repositories)
    {
        var source = new ProviderOptions
        {
            Name = "github",
            Kind = ProviderKind.GitHub,
            BaseUri = new Uri("https://api.example.test/"),
        };
        var configuration = new EffectiveIssueAgentConfiguration(
            new IssueAgentOptions
            {
                Workspace = new WorkspaceOptions { RootPath = Path.GetTempPath() },
                Omp = new OmpOptions { ExecutablePath = "omp" },
            },
            [new EffectiveProviderConfiguration(source, "github", null, repositories)],
            new EffectiveOmpConfiguration("omp", null, null, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>()),
            new EffectiveNotificationsConfiguration(ConfiguredTlsTrustMode.System, [], []));

        return new ProviderRegistry(configuration, new IssueAgentMetrics(), LoggerFactory.Create(_ => { }));
    }

    private static EffectiveRepositoryConfiguration Repository(
        string id,
        string cloneUrl,
        string token,
        IReadOnlyList<string>? additionalCaCertificatePaths = null,
        IReadOnlyList<string>? tlsFingerprints = null,
        IReadOnlyList<string>? sshFingerprints = null,
        ConfiguredTlsTrustMode? tlsMode = null) => new(
        new RepositoryOptions { Id = id, Name = id, CloneUrl = cloneUrl },
        "github",
        ProviderKind.GitHub,
        id,
        "team",
        id,
        cloneUrl,
        null,
        true,
        DateTimeOffset.MinValue,
        false,
        0,
        1,
        1,
        ConfiguredWorkflowMode.Full,
        [],
        new EffectiveGitConfiguration(
            ConfiguredGitAuthenticationMode.Token,
            "x-access-token",
            token,
            null,
            null,
            null,
            tlsMode ?? (additionalCaCertificatePaths is null ? ConfiguredTlsTrustMode.System : ConfiguredTlsTrustMode.SystemPlusAdditionalCa),
            additionalCaCertificatePaths ?? [],
            tlsFingerprints ?? [],
            sshFingerprints is null ? null : ConfiguredSshHostVerificationMode.Pinned,
            sshFingerprints ?? []),
        "Test",
        "test@example.test",
        null,
        new Dictionary<string, string>());
}
