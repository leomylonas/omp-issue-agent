using IssueAgent.Configuration;
using IssueAgent.Git;
using IssueAgent.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void SubmoduleAuthenticationUsesCurrentRepositoryAndRejectsAmbiguousHostMappings()
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

    private static EffectiveRepositoryConfiguration Repository(string id, string cloneUrl, string token) => new(
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
            ConfiguredTlsTrustMode.System,
            [],
            [],
            null,
            []),
        "Test",
        "test@example.test",
        null,
        new Dictionary<string, string>());
}
