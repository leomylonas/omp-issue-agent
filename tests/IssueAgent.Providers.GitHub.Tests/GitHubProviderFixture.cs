using WireMock.Server;

namespace IssueAgent.Providers.GitHub.Tests;

public sealed class GitHubProviderFixture : IDisposable
{
    public GitHubProviderFixture()
    {
        Server = WireMockServer.Start();
        var baseUri = new Uri(Server.Url! + "/");
        Provider = GitHubProviderFactory.Create(new GitHubProviderConfiguration(
            "github",
            baseUri,
            "test-token",
            ["github.example"]));
    }

    public WireMockServer Server { get; }

    public GitHubProvider Provider { get; }

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }
}
