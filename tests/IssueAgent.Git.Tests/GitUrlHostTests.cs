namespace IssueAgent.Git.Tests;

public sealed class GitUrlHostTests
{
    [Theory]
    [InlineData("https://github.com/octo/widgets.git", "github.com")]
    [InlineData("https://gitlab.example.com:8443/group/project.git", "gitlab.example.com")]
    [InlineData("git@github.com:octo/widgets.git", "github.com")]
    [InlineData("ssh://git@gitlab.example.com/group/project.git", "gitlab.example.com")]
    public void TryGetHostExtractsHostFromHttpsAndScpLikeUrls(string url, string expectedHost)
    {
        Assert.Equal(expectedHost, GitUrlHost.TryGetHost(url));
    }

    [Theory]
    [InlineData("https://git.example.test/group/project.git", "https://git.example.test:443")]
    [InlineData("https://git.example.test:8443/group/project.git", "https://git.example.test:8443")]
    [InlineData("ssh://git@git.example.test:2222/group/project.git", "ssh://git.example.test:2222")]
    [InlineData("git@git.example.test:group/project.git", "ssh://git.example.test:22")]
    public void TryGetTransportAuthorityIncludesSchemeAndPort(string url, string expectedAuthority)
    {
        Assert.Equal(expectedAuthority, GitUrlHost.TryGetTransportAuthority(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-url")]
    [InlineData("/local/relative/path")]
    public void TryGetHostReturnsNullForUnrecognizedInput(string? url)
    {
        Assert.Null(GitUrlHost.TryGetHost(url));
    }
}
