using IssueAgent.Providers;
using IssueAgent.Providers.Tests;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IssueAgent.Providers.GitLab.Tests;

public sealed class GitLabProviderContractTests : ProviderContractTests, IClassFixture<GitLabProviderFixture>
{
    private readonly GitLabProviderFixture fixture;

    public GitLabProviderContractTests(GitLabProviderFixture fixture)
    {
        this.fixture = fixture;
        fixture.Server.Reset();
    }

    protected override IGitProvider Provider => fixture.Provider;
    protected override RepositoryRef Repository { get; } = new("123", "123", "");
    protected override Uri TrustedAttachmentUri { get; } = new("https://gitlab.example/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png");
    protected override Uri UntrustedAttachmentUri { get; } = new("https://evil.example/file.png");
    protected override string ValidIssuePayload => """{"iid":7,"title":"Bug","description":"Description","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":[],"assignees":[]}""";

    protected override void StubIssueResponse(int statusCode, string body, TimeSpan? delay = null)
    {
        var response = Response.Create()
            .WithStatusCode(statusCode)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);
        if (delay is { } value) response.WithDelay(value);
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7").UsingGet())
            .RespondWith(response);
    }

    protected override void StubRetryableIssueResponse(string successBody)
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7").UsingGet())
            .InScenario("gitlab-retry")
            .WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7").UsingGet())
            .InScenario("gitlab-retry")
            .WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(successBody));
    }
}
