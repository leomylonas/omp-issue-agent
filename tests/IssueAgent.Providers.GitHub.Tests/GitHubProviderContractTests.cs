using IssueAgent.Providers;
using IssueAgent.Providers.Tests;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IssueAgent.Providers.GitHub.Tests;

public sealed class GitHubProviderContractTests : ProviderContractTests, IClassFixture<GitHubProviderFixture>
{
    private readonly GitHubProviderFixture fixture;

    public GitHubProviderContractTests(GitHubProviderFixture fixture)
    {
        this.fixture = fixture;
        fixture.Server.Reset();
    }

    protected override IGitProvider Provider => fixture.Provider;
    protected override RepositoryRef Repository { get; } = new("github/octo/widgets", "octo", "widgets");
    protected override Uri TrustedAttachmentUri { get; } = new("https://github.example/file.png");
    protected override Uri UntrustedAttachmentUri { get; } = new("https://evil.example/file.png");
    protected override string ValidIssuePayload => """{"number":7,"title":"Bug","body":"Description","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":[],"assignees":[]}""";

    protected override void StubIssueResponse(int statusCode, string body, TimeSpan? delay = null)
    {
        var response = Response.Create()
            .WithStatusCode(statusCode)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);
        if (delay is { } value) response.WithDelay(value);
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7").UsingGet())
            .RespondWith(response);
    }

    protected override void StubRetryableIssueResponse(string successBody)
    {
        var resetAt = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7").UsingGet())
            .InScenario("github-retry")
            .WillSetStateTo("retried")
            .RespondWith(Response.Create()
                .WithStatusCode(403)
                .WithHeader("X-RateLimit-Remaining", "0")
                .WithHeader("X-RateLimit-Reset", resetAt)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"API rate limit exceeded"}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7").UsingGet())
            .InScenario("github-retry")
            .WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(successBody));
    }
}
