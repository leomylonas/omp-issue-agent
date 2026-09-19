using IssueAgent.Providers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IssueAgent.Providers.GitHub.Tests;

public sealed class GitHubProviderTests : IClassFixture<GitHubProviderFixture>
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");

    private readonly GitHubProviderFixture fixture;

    public GitHubProviderTests(GitHubProviderFixture fixture)
    {
        this.fixture = fixture;
        fixture.Server.Reset();
    }

    [Fact]
    public async Task GetCurrentIdentityAsyncReturnsAuthenticatedLoginAndEmail()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/user").UsingGet())
            .RespondWith(JsonResponse("""{"login":"issue-agent-bot","id":1,"name":"IssueAgent Bot","email":"bot@example.com"}"""));

        var identity = await fixture.Provider.GetCurrentIdentityAsync(CancellationToken.None);

        Assert.Equal("issue-agent-bot", identity.Login);
        Assert.Equal("IssueAgent Bot", identity.DisplayName);
        Assert.Equal("bot@example.com", identity.Email);
    }
    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncExcludesPullRequestsAndIssuesBeforeStartDate()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"number":1,"title":"Old issue","created_at":"2023-01-01T00:00:00Z","assignees":[{"login":"issue-agent-bot"}]},
                  {"number":2,"title":"A pull request","created_at":"2024-06-01T00:00:00Z","assignees":[{"login":"issue-agent-bot"}],"pull_request":{"url":"https://example/pulls/2"}},
                  {"number":3,"title":"New issue","created_at":"2024-06-02T00:00:00Z","assignees":[{"login":"issue-agent-bot"}]}
                ]
                """));

        var issues = await CollectAsync(fixture.Provider.DiscoverAssignedOpenIssuesAsync(
            Repository, "issue-agent-bot", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None));

        var issue = Assert.Single(issues);
        Assert.Equal(3, issue.Number);
        Assert.Equal("New issue", issue.Title);
    }

    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncMatchesAssigneesCaseInsensitively()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet())
            .RespondWith(JsonResponse("""
                [{"number":8,"title":"Case variant","created_at":"2024-06-02T00:00:00Z",
                  "assignees":[{"login":"Issue-Agent-Bot"}]}]
                """));

        var issues = await CollectAsync(fixture.Provider.DiscoverAssignedOpenIssuesAsync(
            Repository, "issue-agent-bot", DateTimeOffset.MinValue, CancellationToken.None));

        var issue = Assert.Single(issues);
        Assert.Contains("Issue-Agent-Bot", issue.Assignees);
    }

    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncFollowsLinkHeaderPaginationAcrossPages()
    {
        var page2Url = $"{fixture.Server.Url}/api/v3/repos/octo/widgets/issues?page=2";
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").WithParam("page", "2").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"number":21,"title":"Second page issue","created_at":"2024-06-03T00:00:00Z","assignees":[{"login":"issue-agent-bot"}]}
                ]
                """));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Link", $"<{page2Url}>; rel=\"next\"")
                .WithBody("""
                    [
                      {"number":20,"title":"First page issue","created_at":"2024-06-02T00:00:00Z","assignees":[{"login":"issue-agent-bot"}]}
                    ]
                    """));

        var issues = await CollectAsync(fixture.Provider.DiscoverAssignedOpenIssuesAsync(
            Repository, "issue-agent-bot", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None));

        Assert.Equal([20, 21], issues.Select(i => i.Number).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task DiscoverManagedIssuesAsyncIncludesClosedLabeledIssuesOnly()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"number":4,"title":"Ordinary closed issue","created_at":"2024-06-01T00:00:00Z","labels":[],"assignees":[]},
                  {"number":5,"title":"Managed closed issue","created_at":"2024-06-02T00:00:00Z","labels":[{"name":"agent:phase:review"}],"assignees":[]}
                ]
                """));

        var issues = await CollectAsync(fixture.Provider.DiscoverManagedIssuesAsync(Repository, CancellationToken.None));

        Assert.Equal(5, Assert.Single(issues).Number);
    }

    [Fact]
    public async Task GetIssueAsyncMapsLabelsAssigneesAndBody()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7").UsingGet())
            .RespondWith(JsonResponse("""
                {
                  "number":7,"title":"Bug report","body":"Steps to reproduce",
                  "created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-02T00:00:00Z",
                  "labels":[{"name":"bug"},{"name":"agent:phase:planning"}],
                  "assignees":[{"login":"issue-agent-bot"}]
                }
                """));

        var issue = await fixture.Provider.GetIssueAsync(Repository, 7, CancellationToken.None);

        Assert.Equal("Bug report", issue.Title);
        Assert.Equal("Steps to reproduce", issue.Description);
        Assert.Contains("bug", issue.Labels);
        Assert.Contains("issue-agent-bot", issue.Assignees);
    }

    [Fact]
    public async Task GetIssueAsyncRetriesTransientServerErrors()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7").UsingGet())
            .InScenario("github-5xx")
            .WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(503).WithBody("""{"message":"temporary"}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7").UsingGet())
            .InScenario("github-5xx")
            .WhenStateIs("retried")
            .RespondWith(JsonResponse("""{"number":7,"title":"Recovered","body":"ok","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":[],"assignees":[]}"""));

        var issue = await fixture.Provider.GetIssueAsync(Repository, 7, CancellationToken.None);

        Assert.Equal("Recovered", issue.Title);
    }

    [Fact]
    public async Task GetDefaultBranchAsyncReturnsTheRepositorysConfiguredDefaultBranch()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets").UsingGet())
            .RespondWith(JsonResponse("""{"id":1,"name":"widgets","full_name":"octo/widgets","default_branch":"develop"}"""));

        var defaultBranch = await fixture.Provider.GetDefaultBranchAsync(Repository, CancellationToken.None);

        Assert.Equal("develop", defaultBranch);
    }

    [Fact]
    public async Task CreateAndUpdateIssueCommentAsyncRoundTripBody()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7/comments").UsingPost())
            .RespondWith(JsonResponse("""{"id":100,"user":{"login":"issue-agent-bot"},"body":"hello","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z"}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/comments/100").UsingPatch())
            .RespondWith(JsonResponse("""{"id":100,"user":{"login":"issue-agent-bot"},"body":"updated","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-02T00:00:00Z"}"""));

        var created = await fixture.Provider.CreateIssueCommentAsync(Repository, 7, "hello", CancellationToken.None);
        var updated = await fixture.Provider.UpdateIssueCommentAsync(Repository, 7, created.Id, "updated", CancellationToken.None);

        Assert.Equal(100, created.Id);
        Assert.Equal("hello", created.Body);
        Assert.Equal("updated", updated.Body);
    }
    [Fact]
    public async Task UpdateIssueCommentAsyncRetriesTransientServerErrors()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/comments/100").UsingPatch())
            .InScenario("comment-update-retry")
            .WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503).WithBody("""{"message":"temporary"}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/comments/100").UsingPatch())
            .InScenario("comment-update-retry")
            .WhenStateIs("recovered")
            .RespondWith(JsonResponse("""{"id":100,"user":{"login":"issue-agent-bot"},"body":"updated","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-02T00:00:00Z"}"""));

        var updated = await fixture.Provider.UpdateIssueCommentAsync(Repository, 7, 100, "updated", CancellationToken.None);

        Assert.Equal("updated", updated.Body);
    }


    [Fact]
    public async Task GetIssueCommentsAsyncFlagsBotAuthorsByLoginSuffix()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7/comments").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"id":1,"user":{"login":"alice"},"body":"human comment","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z"},
                  {"id":2,"user":{"login":"dependabot[bot]"},"body":"bot comment","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z"}
                ]
                """));

        var comments = await CollectAsync(fixture.Provider.GetIssueCommentsAsync(Repository, 7, CancellationToken.None));

        Assert.False(comments.Single(c => c.AuthorLogin == "alice").IsBot);
        Assert.True(comments.Single(c => c.AuthorLogin == "dependabot[bot]").IsBot);
    }

    [Fact]
    public async Task EnsureLabelAsyncCreatesOnlyWhenMissing()
    {
        fixture.Server
            .Given(Request.Create().WithPath(p => p != null && p.Contains("/labels/agent", StringComparison.Ordinal)).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithHeader("Content-Type", "application/json").WithBody("""{"message":"Not Found"}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/labels").UsingPost())
            .RespondWith(JsonResponse("""{"name":"agent:phase:planning","color":"ededed","description":"Planning"}"""));

        await fixture.Provider.EnsureLabelAsync(Repository, new ProviderLabel("agent:phase:planning", "ededed", "Planning"), CancellationToken.None);

        Assert.Equal(1, fixture.Server.LogEntries.Count(e => e.RequestMessage!.Path == "/api/v3/repos/octo/widgets/labels" && e.RequestMessage.Method == "POST"));
    }

    [Fact]
    public async Task EnsureLabelAsyncDoesNotCreateWhenLabelAlreadyExists()
    {
        fixture.Server
            .Given(Request.Create().WithPath(p => p != null && p.Contains("/labels/agent", StringComparison.Ordinal)).UsingGet())
            .RespondWith(JsonResponse("""{"name":"agent:phase:planning","color":"custom","description":"user edited"}"""));

        await fixture.Provider.EnsureLabelAsync(Repository, new ProviderLabel("agent:phase:planning", "ededed", "Planning"), CancellationToken.None);

        Assert.DoesNotContain(fixture.Server.LogEntries, e => e.RequestMessage!.Method == "POST");
    }

    [Fact]
    public async Task FindMergeRequestAsyncReturnsNullWhenNoneExists()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls").UsingGet())
            .RespondWith(JsonResponse("[]"));

        var result = await fixture.Provider.FindMergeRequestAsync(Repository, "agent/issue-7", "main", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateDraftMergeRequestAsyncSendsDraftFlagAndMapsResult()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls").UsingPost()
                .WithBody(b => b != null && b.Contains("\"draft\":true", StringComparison.Ordinal)))
            .RespondWith(JsonResponse("""
                {"number":9,"head":{"ref":"agent/issue-7-fix"},"base":{"ref":"main"},"title":"Fix bug","body":"Implements the plan","draft":true,"merged":false,"state":"open"}
                """));

        var result = await fixture.Provider.CreateDraftMergeRequestAsync(
            new CreateMergeRequestRequest(Repository, "agent/issue-7-fix", "main", "Fix bug", "Implements the plan", true, 7),
            CancellationToken.None);

        Assert.Equal(9, result.Number);
        Assert.True(result.IsDraft);
        Assert.False(result.IsMerged);
        Assert.False(result.IsClosed);
    }

    [Fact]
    public async Task GetReviewThreadsAsyncParsesResolvedAndUnresolvedThreadsFromGraphQl()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/graphql").UsingPost())
            .RespondWith(JsonResponse("""
                {
                  "data": {
                    "repository": {
                      "pullRequest": {
                        "reviewThreads": {
                          "pageInfo": {"hasNextPage": false, "endCursor": null},
                          "nodes": [
                            {
                              "id": "thread-1",
                              "isResolved": true,
                              "comments": {"nodes": [{"databaseId": 501, "body": "fixed now", "createdAt": "2024-01-01T00:00:00Z", "updatedAt": "2024-01-01T00:00:00Z", "author": {"login": "alice"}}]}
                            },
                            {
                              "id": "thread-2",
                              "isResolved": false,
                              "comments": {"nodes": [{"databaseId": 502, "body": "please address", "createdAt": "2024-01-01T00:00:00Z", "updatedAt": "2024-01-01T00:00:00Z", "author": {"login": "bob"}}]}
                            }
                          ]
                        }
                      }
                    }
                  }
                }
                """));

        var threads = await CollectAsync(fixture.Provider.GetReviewThreadsAsync(Repository, 9, CancellationToken.None));

        Assert.True(threads.Single(t => t.Id == "thread-1").IsResolved);
        Assert.False(threads.Single(t => t.Id == "thread-2").IsResolved);
    }

    [Fact]
    public async Task GetIssueRelationshipsAsyncParsesCrossReferencedTimelineEvents()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7/timeline").WithParam("page", "1").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"event":"commented"},
                  {"event":"cross-referenced","source":{"issue":{"number":42,"repository":{"name":"widgets","owner":{"login":"octo"}}}}}
                ]
                """));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/7/timeline").WithParam("page", "2").UsingGet())
            .RespondWith(JsonResponse("[]"));

        var relationships = await CollectAsync(fixture.Provider.GetIssueRelationshipsAsync(Repository, 7, CancellationToken.None));

        var relationship = Assert.Single(relationships);
        Assert.Equal("related", relationship.Relationship);
        Assert.Equal(42, relationship.IssueNumber);
        Assert.Equal(Repository, relationship.Repository);
    }

    [Fact]
    public void IsTrustedAttachmentHostAcceptsConfiguredSuffixesOnly()
    {
        Assert.True(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://githubusercontent.example/foo.png")));
        Assert.True(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://raw.githubusercontent.example/foo.png")));
        Assert.False(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://evil.example/foo.png")));
    }

    [Fact]
    public async Task DownloadAttachmentAsyncSendsAuthorizationOnlyForTrustedHosts()
    {
        var serverUri = new Uri(fixture.Server.Url!);
        var provider = GitHubProviderFactory.Create(new GitHubProviderConfiguration(
            "github", new Uri(fixture.Server.Url! + "/"), "secret-token", [serverUri.Host]));
        fixture.Server
            .Given(Request.Create().WithPath("/files/report.pdf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/pdf").WithBody("pdf-bytes"));

        var trustedDestination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var trustedAttachment = new ProviderAttachment(new Uri(fixture.Server.Url! + "/files/report.pdf"), "report.pdf", null, new AttachmentSource("issue-description", "7"), true);
        await provider.DownloadAttachmentAsync(trustedAttachment, trustedDestination, 1024, CancellationToken.None);
        var trustedRequest = fixture.Server.LogEntries.Single(e => e.RequestMessage!.Path == "/files/report.pdf");
        Assert.True(trustedRequest.RequestMessage!.Headers!.ContainsKey("Authorization"));

        // Same server, an untrusted hostname alias (127.0.0.1 vs "localhost"): the suffix list only
        // trusts serverUri.Host, so a request to the loopback IP literal must never receive the token.
        var untrustedHost = serverUri.Host == "127.0.0.1" ? "localhost" : "127.0.0.1";
        var untrustedUri = new UriBuilder(serverUri) { Host = untrustedHost, Path = "/files/report.pdf" }.Uri;
        var untrustedDestination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var untrustedAttachment = new ProviderAttachment(untrustedUri, "report.pdf", null, new AttachmentSource("issue-description", "7"), false, System.Net.Dns.GetHostAddresses(untrustedHost).ToHashSet());
        await provider.DownloadAttachmentAsync(untrustedAttachment, untrustedDestination, 1024, CancellationToken.None);
        var untrustedRequest = fixture.Server.LogEntries.Last(e => e.RequestMessage!.Path == "/files/report.pdf");
        Assert.False(untrustedRequest.RequestMessage!.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task DownloadAttachmentAsyncThrowsWhenDeclaredContentLengthExceedsLimit()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/files/huge.zip").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/zip").WithBody(new string('a', 2000)));

        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var attachment = new ProviderAttachment(new Uri(fixture.Server.Url! + "/files/huge.zip"), "huge.zip", null, new AttachmentSource("issue-description", "7"), false, System.Net.Dns.GetHostAddresses(new Uri(fixture.Server.Url!).Host).ToHashSet());

        await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
            fixture.Provider.DownloadAttachmentAsync(attachment, destination, 1024, CancellationToken.None).AsTask());
    }

    private static WireMock.ResponseBuilders.IResponseBuilder JsonResponse(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }
}
