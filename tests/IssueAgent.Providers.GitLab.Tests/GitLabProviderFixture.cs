using WireMock.Server;

namespace IssueAgent.Providers.GitLab.Tests;

public sealed class GitLabProviderFixture : IDisposable
{
    public GitLabProviderFixture()
    {
        Server = WireMockServer.Start();
        var baseUri = new Uri(Server.Url! + "/api/v4/");
        Provider = GitLabProviderFactory.Create(new GitLabProviderConfiguration(
            "gitlab",
            baseUri,
            null,
            ["gitlab.example"]));
    }

    public WireMockServer Server { get; }

    public GitLabProvider Provider { get; }

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }
}
