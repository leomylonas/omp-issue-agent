using System.Net;

using IssueAgent.Domain;
using IssueAgent.Providers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IssueAgent.Providers.GitLab.Tests;

public sealed class GitLabProviderTests : IClassFixture<GitLabProviderFixture>
{
    private static readonly RepositoryRef Repository = new("123", "octo", "widgets");

    private readonly GitLabProviderFixture fixture;

    public GitLabProviderTests(GitLabProviderFixture fixture)
    {
        this.fixture = fixture;
        fixture.Server.Reset();
    }

    [Fact]
    public async Task GetCurrentIdentityAsyncPrefersGitLabsCommitEmail()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/user").UsingGet())
            .RespondWith(JsonResponse("""{"id":1,"username":"issue-agent-bot","name":"IssueAgent Bot","email":"profile@example.com","commit_email":"12345-issue-agent-bot@users.noreply.gitlab.com"}"""));

        var identity = await fixture.Provider.GetCurrentIdentityAsync(CancellationToken.None);

        Assert.Equal("issue-agent-bot", identity.Login);
        Assert.Equal("IssueAgent Bot", identity.DisplayName);
        Assert.Equal("12345-issue-agent-bot@users.noreply.gitlab.com", identity.Email);
    }

    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncExcludesIssuesBeforeStartDate()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"iid":1,"title":"Old issue","created_at":"2023-01-01T00:00:00Z","updated_at":"2023-01-01T00:00:00Z","labels":[],"assignees":[{"username":"issue-agent-bot"}]},
                  {"iid":2,"title":"New issue","created_at":"2024-06-02T00:00:00Z","updated_at":"2024-06-02T00:00:00Z","labels":[],"assignees":[{"username":"issue-agent-bot"}]}
                ]
                """));

        var issues = await CollectAsync(fixture.Provider.DiscoverAssignedOpenIssuesAsync(
            Repository, "issue-agent-bot", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None));

        var issue = Assert.Single(issues);
        Assert.Equal(2, issue.Number);
    }

    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncMatchesAssigneesCaseInsensitively()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet())
            .RespondWith(JsonResponse("""
                [{"iid":8,"title":"Case variant","created_at":"2024-06-02T00:00:00Z",
                  "updated_at":"2024-06-02T00:00:00Z","labels":[],
                  "assignees":[{"username":"Issue-Agent-Bot"}]}]
                """));

        var issues = await CollectAsync(fixture.Provider.DiscoverAssignedOpenIssuesAsync(
            Repository, "issue-agent-bot", DateTimeOffset.MinValue, CancellationToken.None));

        var issue = Assert.Single(issues);
        Assert.Contains("Issue-Agent-Bot", issue.Assignees);
    }

    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncFollowsLinkHeaderPagination()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Link", $"<{fixture.Server.Url}/api/v4/projects/123/issues?page=2>; rel=\"next\"")
                .WithBody("""[{"iid":1,"title":"Page one","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":[],"assignees":[{"username":"issue-agent-bot"}]}]"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues").WithParam("page", "2").UsingGet())
            .RespondWith(JsonResponse("""[{"iid":2,"title":"Page two","created_at":"2024-01-02T00:00:00Z","updated_at":"2024-01-02T00:00:00Z","labels":[],"assignees":[{"username":"issue-agent-bot"}]}]"""));

        var issues = await CollectAsync(fixture.Provider.DiscoverAssignedOpenIssuesAsync(
            Repository, "issue-agent-bot", DateTimeOffset.MinValue, CancellationToken.None));

        Assert.Equal([1L, 2L], issues.Select(i => i.Number).OrderBy(n => n));
    }

    [Theory]
    [InlineData("<%%%>; rel=\"next\"", "malformed")]
    [InlineData("<https://attacker.example/api/v4/projects/123/issues?page=2>; rel=\"next\"", "different authority")]
    public async Task DiscoverAssignedOpenIssuesAsyncRejectsUnsafePaginationLinks(string link, string reason)
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Link", link)
                .WithBody("[]"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(fixture.Provider.DiscoverAssignedOpenIssuesAsync(
            Repository, "issue-agent-bot", DateTimeOffset.MinValue, CancellationToken.None)));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.Single(fixture.Server.LogEntries);
    }

    [Fact]
    public async Task DiscoverManagedIssuesAsyncIncludesClosedLabeledIssuesWithoutAssigneeFilter()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"iid":4,"title":"Ordinary closed issue","created_at":"2024-06-01T00:00:00Z","updated_at":"2024-06-01T00:00:00Z","labels":[],"assignees":[]},
                  {"iid":5,"title":"Managed closed issue","created_at":"2024-06-02T00:00:00Z","updated_at":"2024-06-02T00:00:00Z","labels":["agent:phase:review"],"assignees":[]}
                ]
                """));

        var issues = await CollectAsync(fixture.Provider.DiscoverManagedIssuesAsync(Repository, CancellationToken.None));

        Assert.Equal(5, Assert.Single(issues).Number);
    }

    [Fact]
    public async Task DiscoverManagedIssuesAsyncRejectsRepeatedPageUrlsInsteadOfLoopingForever()
    {
        // Repeated-URL protection prevents a malformed Link header from causing an infinite walk,
        // while legitimate pagination remains uncapped.
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Link", $"<{fixture.Server.Url}/api/v4/projects/123/issues?page=2>; rel=\"next\"")
                .WithBody("""[{"iid":1,"title":"t","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":[],"assignees":[]}]"""));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectAsync(fixture.Provider.DiscoverManagedIssuesAsync(Repository, CancellationToken.None)));

        Assert.Contains("repeated page URL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetIssueAsyncMapsDescriptionLabelsAndAssignees()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7").UsingGet())
            .RespondWith(JsonResponse("""
                {"iid":7,"title":"Bug report","description":"Steps to reproduce","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-02T00:00:00Z","labels":["bug"],"assignees":[{"username":"issue-agent-bot"}]}
                """));

        var issue = await fixture.Provider.GetIssueAsync(Repository, 7, CancellationToken.None);

        Assert.Equal("Bug report", issue.Title);
        Assert.Equal("Steps to reproduce", issue.Description);
        Assert.Contains("bug", issue.Labels);
    }

    [Fact]
    public async Task GetDefaultBranchAsyncReturnsTheProjectsConfiguredDefaultBranch()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123").UsingGet())
            .RespondWith(JsonResponse("""{"id":123,"default_branch":"develop"}"""));

        var defaultBranch = await fixture.Provider.GetDefaultBranchAsync(Repository, CancellationToken.None);

        Assert.Equal("develop", defaultBranch);
    }

    [Fact]
    public async Task GetIssueCommentsAsyncSkipsSystemNotesAndFlagsBotAuthors()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/notes").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"id":1,"body":"assigned to alice","author":{"username":"root"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":true},
                  {"id":2,"body":"human comment","author":{"username":"alice"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false},
                  {"id":3,"body":"bot comment","author":{"username":"project-bot"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false}
                ]
                """));

        var comments = await CollectAsync(fixture.Provider.GetIssueCommentsAsync(Repository, 7, CancellationToken.None));

        Assert.Equal(2, comments.Count);
        Assert.False(comments.Single(c => c.AuthorLogin == "alice").IsBot);
        Assert.True(comments.Single(c => c.AuthorLogin == "project-bot").IsBot);
    }

    [Fact]
    public async Task CreateAndUpdateIssueCommentAsyncRoundTripBody()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/notes").UsingPost())
            .RespondWith(JsonResponse("""{"id":100,"body":"hello","author":{"username":"issue-agent-bot"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/notes/100").UsingPut())
            .RespondWith(JsonResponse("""{"id":100,"body":"updated","author":{"username":"issue-agent-bot"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-02T00:00:00Z","system":false}"""));

        var created = await fixture.Provider.CreateIssueCommentAsync(Repository, 7, "hello", CancellationToken.None);
        var updated = await fixture.Provider.UpdateIssueCommentAsync(Repository, 7, created.Id, "updated", CancellationToken.None);

        Assert.Equal("hello", created.Body);
        Assert.Equal("updated", updated.Body);
    }

    [Fact]
    public async Task EnsureLabelAsyncCreatesOnlyWhenMissing()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/labels").UsingGet())
            .RespondWith(JsonResponse("[]"));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/labels").UsingPost())
            .RespondWith(JsonResponse("""{"name":"agent:phase:planning","color":"#ededed","description":"Planning"}"""));

        await fixture.Provider.EnsureLabelAsync(Repository, new ProviderLabel("agent:phase:planning", "ededed", "Planning"), CancellationToken.None);

        Assert.Single(fixture.Server.LogEntries, e => e.RequestMessage!.Method == "POST");
    }

    [Fact]
    public async Task EnsureLabelAsyncAcceptsDuplicateCreateWhenLabelAppearsConcurrently()
    {
        const string labelsPath = "/api/v4/projects/123/labels";
        fixture.Server
            .Given(Request.Create().WithPath(labelsPath).UsingGet())
            .InScenario("gitlab-label-create-race")
            .WillSetStateTo("creating")
            .RespondWith(JsonResponse("[]"));
        fixture.Server
            .Given(Request.Create().WithPath(labelsPath).UsingPost())
            .InScenario("gitlab-label-create-race")
            .WhenStateIs("creating")
            .WillSetStateTo("created")
            .RespondWith(Response.Create().WithStatusCode(409).WithHeader("Content-Type", "application/json").WithBody("""{"message":"Label already exists"}"""));
        fixture.Server
            .Given(Request.Create().WithPath(labelsPath).UsingGet())
            .InScenario("gitlab-label-create-race")
            .WhenStateIs("created")
            .RespondWith(JsonResponse("""[{"name":"agent:phase:planning","color":"#custom","description":"created concurrently"}]"""));

        await fixture.Provider.EnsureLabelAsync(Repository, new ProviderLabel("agent:phase:planning", "ededed", "Planning"), CancellationToken.None);

        Assert.Equal(2, fixture.Server.LogEntries.Count(e => e.RequestMessage!.Path == labelsPath && e.RequestMessage.Method == "GET"));
        Assert.Single(fixture.Server.LogEntries, e => e.RequestMessage!.Path == labelsPath && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public async Task EnsureLabelAsyncDoesNotCreateWhenLabelAlreadyExists()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/labels").UsingGet())
            .RespondWith(JsonResponse("""[{"name":"agent:phase:planning","color":"#custom","description":"user edited"}]"""));

        await fixture.Provider.EnsureLabelAsync(Repository, new ProviderLabel("agent:phase:planning", "ededed", "Planning"), CancellationToken.None);

        Assert.DoesNotContain(fixture.Server.LogEntries, e => e.RequestMessage!.Method == "POST");
    }

    [Fact]
    public async Task EnsureLabelAsyncFindsExistingLabelOnLaterPage()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/labels").WithParam("per_page", "100").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Link", $"<{fixture.Server.Url}/api/v4/projects/123/labels?search=agent%3Aphase%3Aplanning&page=2>; rel=\"next\"")
                .WithBody("[]"));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/labels").WithParam("page", "2").UsingGet())
            .RespondWith(JsonResponse("""[{"name":"agent:phase:planning","color":"#custom","description":"user edited"}]"""));

        await fixture.Provider.EnsureLabelAsync(Repository, new ProviderLabel("agent:phase:planning", "ededed", "Planning"), CancellationToken.None);

        Assert.DoesNotContain(fixture.Server.LogEntries, entry => entry.RequestMessage!.Method == "POST");
        Assert.Equal(2, fixture.Server.LogEntries.Count(entry => entry.RequestMessage!.Path == "/api/v4/projects/123/labels"));
    }

    [Fact]
    public async Task AddLabelsAsyncUpdatesIssueEndpointForIssueWorkItems()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7").UsingPut()
                .WithBody(b => b != null && b.Contains("\"add_labels\":\"agent:phase:planned\"", StringComparison.Ordinal)))
            .RespondWith(JsonResponse("""{"iid":7,"title":"t","description":"d","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":["agent:phase:planned"],"assignees":[]}"""));

        await fixture.Provider.AddLabelsAsync(new ProviderWorkItemReference(Repository, ProviderWorkItemKind.Issue, 7), ["agent:phase:planned"], CancellationToken.None);

        Assert.Single(fixture.Server.LogEntries, e => e.RequestMessage!.Path == "/api/v4/projects/123/issues/7" && e.RequestMessage.Method == "PUT");
    }

    [Fact]
    public async Task AddLabelsAsyncUpdatesMergeRequestEndpointForMergeRequestWorkItems()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/merge_requests/9").UsingPut()
                .WithBody(b => b != null && b.Contains("\"add_labels\":\"agent:phase:review\"", StringComparison.Ordinal)))
            .RespondWith(JsonResponse("""{"iid":9,"source_branch":"agent/issue-7","target_branch":"main","title":"t","description":"d","draft":false,"state":"opened","labels":["agent:phase:review"]}"""));

        await fixture.Provider.AddLabelsAsync(new ProviderWorkItemReference(Repository, ProviderWorkItemKind.MergeRequest, 9), ["agent:phase:review"], CancellationToken.None);

        Assert.Single(fixture.Server.LogEntries, e => e.RequestMessage!.Path == "/api/v4/projects/123/merge_requests/9" && e.RequestMessage.Method == "PUT");
        Assert.DoesNotContain(fixture.Server.LogEntries, e => e.RequestMessage!.Path == "/api/v4/projects/123/issues/9");
    }

    [Fact]
    public async Task FindMergeRequestAsyncReturnsNullWhenNoneExists()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/merge_requests").UsingGet())
            .RespondWith(JsonResponse("[]"));

        var result = await fixture.Provider.FindMergeRequestAsync(Repository, "agent/issue-7", "main", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindMergeRequestAsyncMapsProviderWebUrl()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/merge_requests").UsingGet())
            .RespondWith(JsonResponse("""
                [{"iid":9,"web_url":"https://gitlab.example/octo/widgets/-/merge_requests/9","source_branch":"agent/issue-7","target_branch":"main","title":"Fix bug","description":"Implements the plan","draft":true,"state":"opened","labels":[]}]
                """));

        var result = await fixture.Provider.FindMergeRequestAsync(Repository, "agent/issue-7", "main", CancellationToken.None);

        Assert.Equal(new Uri("https://gitlab.example/octo/widgets/-/merge_requests/9"), result!.WebUrl);
    }

    [Fact]
    public async Task CreateDraftMergeRequestAsyncPrefixesTitleAndParsesDraftFlag()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/merge_requests").UsingPost()
                .WithBody(b => b != null && b.Contains("\"title\":\"Draft: Fix bug\"", StringComparison.Ordinal)))
            .RespondWith(JsonResponse("""{"iid":9,"web_url":"https://gitlab.example/octo/widgets/-/merge_requests/9","source_branch":"agent/issue-7-fix","target_branch":"main","title":"Draft: Fix bug","description":"Implements the plan","draft":true,"state":"opened","labels":[]}"""));

        var result = await fixture.Provider.CreateDraftMergeRequestAsync(
            new CreateMergeRequestRequest(Repository, "agent/issue-7-fix", "main", "Fix bug", "Implements the plan", true, 7),
            CancellationToken.None);

        Assert.Equal(9, result.Number);
        Assert.Equal(new Uri("https://gitlab.example/octo/widgets/-/merge_requests/9"), result.WebUrl);
        Assert.True(result.IsDraft);
    }

    [Fact]
    public async Task GetReviewThreadsAsyncMapsResolvedStateAndSkipsIndividualNotes()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/merge_requests/9/discussions").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"id":"disc-1","individual_note":false,"notes":[{"id":501,"body":"fixed now","author":{"username":"alice"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false,"resolvable":true,"resolved":true}]},
                  {"id":"disc-2","individual_note":false,"notes":[{"id":502,"body":"please address","author":{"username":"bob"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false,"resolvable":true,"resolved":false}]},
                  {"id":"disc-3","individual_note":true,"notes":[{"id":503,"body":"note only","author":{"username":"carol"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false,"resolvable":false,"resolved":false}]}
                ]
                """));

        var threads = await CollectAsync(fixture.Provider.GetReviewThreadsAsync(Repository, 9, CancellationToken.None));

        Assert.Equal(2, threads.Count);
        Assert.True(threads.Single(t => t.Id == "disc-1").IsResolved);
        Assert.False(threads.Single(t => t.Id == "disc-2").IsResolved);
    }

    [Fact]
    public async Task GetIssueRelationshipsAsyncMapsGitLabLinkTypes()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/links").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"iid":42,"project_id":123,"link_type":"relates_to"},
                  {"iid":43,"project_id":123,"link_type":"blocks"},
                  {"iid":44,"project_id":123,"link_type":"is_blocked_by"}
                ]
                """));

        var relationships = await CollectAsync(fixture.Provider.GetIssueRelationshipsAsync(Repository, 7, CancellationToken.None));

        Assert.Equal("related", relationships.Single(r => r.IssueNumber == 42).Relationship);
        Assert.Equal("blocks", relationships.Single(r => r.IssueNumber == 43).Relationship);
        Assert.Equal("blocked-by", relationships.Single(r => r.IssueNumber == 44).Relationship);
    }

    [Fact]
    public async Task GetIssueRelationshipsAsyncEmitsAGitLabApiConsumableIdForACrossProjectLink()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/links").UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"iid":42,"project_id":456,"link_type":"relates_to","web_url":"https://gitlab.example/other/project/-/issues/42"}
                ]
                """));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/456/issues/42").UsingGet())
            .RespondWith(JsonResponse("""{"iid":42,"title":"Other issue","description":"d","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":[],"assignees":[]}"""));

        var relationships = await CollectAsync(fixture.Provider.GetIssueRelationshipsAsync(Repository, 7, CancellationToken.None));
        var linked = Assert.Single(relationships);

        Assert.Equal("456", linked.Repository.Id);
        Assert.Equal("other", linked.Repository.OwnerOrNamespace);
        Assert.Equal("project", linked.Repository.Name);

        // The synthesized Id must be directly usable as a GitLab API project identifier: fetching
        // the linked issue with it must succeed rather than 404 (the exact defect this regression
        // test defends against).
        var linkedIssue = await fixture.Provider.GetIssueAsync(linked.Repository, linked.IssueNumber, CancellationToken.None);
        Assert.Equal("Other issue", linkedIssue.Title);
    }

    [Fact]
    public async Task GetIssueRelationshipsAsyncTreatsASameProjectLinkAsTheRootRepositoryEvenUnderThePathIdConvention()
    {
        var namespacedRepository = new RepositoryRef("octo/widgets", "octo", "widgets");
        fixture.Server
            .Given(Request.Create().WithPath(p => p != null && p.Contains("issues/7/links", StringComparison.Ordinal) && p.Contains("widgets", StringComparison.Ordinal)).UsingGet())
            .RespondWith(JsonResponse("""
                [
                  {"iid":9,"project_id":123,"link_type":"blocks","web_url":"https://gitlab.example/octo/widgets/-/issues/9"}
                ]
                """));

        var relationships = await CollectAsync(fixture.Provider.GetIssueRelationshipsAsync(namespacedRepository, 7, CancellationToken.None));
        var linked = Assert.Single(relationships);

        // Same project as root (matched by owner/name parsed from web_url, since the numeric
        // project_id never equals the root's path-form Id): must reuse the root ref exactly, not a
        // different alias for the same project.
        Assert.Same(namespacedRepository, linked.Repository);
    }

    [Fact]
    public async Task ProjectIdWithNamespaceSlashIsUrlEncoded()
    {
        var namespacedRepository = new RepositoryRef("octo/widgets", "octo", "widgets");
        fixture.Server
            .Given(Request.Create().WithPath(p => p != null && p.Contains("issues/7", StringComparison.Ordinal) && p.Contains("widgets", StringComparison.Ordinal)).UsingGet())
            .RespondWith(JsonResponse("""{"iid":7,"title":"t","description":"d","created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","labels":[],"assignees":[]}"""));

        var issue = await fixture.Provider.GetIssueAsync(namespacedRepository, 7, CancellationToken.None);

        Assert.Equal(7, issue.Number);
        Assert.Contains(fixture.Server.LogEntries, e => e.RequestMessage!.AbsoluteUrl.Contains("%2F", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsTrustedAttachmentHostRequiresDocumentedGitLabAttachmentPath()
    {
        Assert.True(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
        Assert.True(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example/-/project/123/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
        Assert.True(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example/-/group/123/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
        Assert.False(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example/api/v4/projects/123/issues/7")));
        Assert.False(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example/octo/widgets/-/issues/7/file.png")));
        Assert.False(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://uploads.gitlab.example/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
        Assert.False(fixture.Provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example:8443/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
        Assert.False(fixture.Provider.IsTrustedAttachmentHost(new Uri("http://gitlab.example/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
    }

    [Fact]
    public void IsTrustedAttachmentHostSupportsConfiguredEnterpriseAuthorityAndBasePath()
    {
        var provider = GitLabProviderFactory.Create(new GitLabProviderConfiguration(
            "gitlab",
            new Uri("https://gitlab.example:8443/gitlab/api/v4/"),
            "test-token",
            ["gitlab.example:8443"]));

        Assert.True(provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example:8443/gitlab/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
        Assert.False(provider.IsTrustedAttachmentHost(new Uri("https://gitlab.example:8443/api/v4/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png")));
    }

    [Fact]
    public void ResolveAttachmentUrlResolvesOnlyDocumentedRelativeGitLabUploads()
    {
        var provider = GitLabProviderFactory.Create(new GitLabProviderConfiguration(
            "gitlab",
            new Uri("https://gitlab.example:8443/gitlab/api/v4/"),
            "test-token",
            ["gitlab.example:8443"]));

        var rootMarkdownUpload = Assert.Single(MarkdownAttachmentScanner.ScanLinks(
            "[file](/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png)"));
        var projectMarkdownUpload = Assert.Single(MarkdownAttachmentScanner.ScanLinks(
            "[file](/-/project/123/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png)"));
        var rootUpload = provider.ResolveAttachmentUrl(rootMarkdownUpload);
        var projectUpload = provider.ResolveAttachmentUrl(projectMarkdownUpload);

        Assert.Equal("https://gitlab.example:8443/gitlab/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png", rootUpload?.ToString());
        Assert.Equal("https://gitlab.example:8443/gitlab/-/project/123/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png", projectUpload?.ToString());
        Assert.True(provider.IsTrustedAttachmentHost(rootUpload!));
        Assert.True(provider.IsTrustedAttachmentHost(projectUpload!));
        Assert.Null(provider.ResolveAttachmentUrl(new Uri("/docs/guide.pdf", UriKind.Relative)));
        Assert.Null(provider.ResolveAttachmentUrl(new Uri("//evil.example/uploads/66dbcd21ec5d24ed6ea225176098d52b/file.png", UriKind.Relative)));
    }

    [Fact]
    public async Task DurableWorkflowCheckpointResumesAfterProviderRestartWithoutCreatingDuplicateResources()
    {
        const string canonicalBody = "<!-- issue-agent:state -->";
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/notes").UsingPost())
            .RespondWith(JsonResponse("""{"id":100,"body":"<!-- issue-agent:state -->","author":{"username":"issue-agent-bot"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/notes").UsingGet())
            .RespondWith(JsonResponse("""[{"id":100,"body":"<!-- issue-agent:state -->","author":{"username":"issue-agent-bot"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-01T00:00:00Z","system":false}]"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/issues/7/notes/100").UsingPut())
            .RespondWith(JsonResponse("""{"id":100,"body":"<!-- issue-agent:state -->\nreview ready","author":{"username":"issue-agent-bot"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2024-01-02T00:00:00Z","system":false}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/merge_requests").UsingPost())
            .RespondWith(JsonResponse("""{"iid":9,"web_url":"https://gitlab.example/octo/widgets/-/merge_requests/9","source_branch":"agent/issue-7","target_branch":"main","title":"Draft: Fix","description":"body","draft":true,"state":"opened","labels":[]}"""));
        fixture.Server
            .Given(Request.Create().WithPath("/api/v4/projects/123/merge_requests").UsingGet())
            .RespondWith(JsonResponse("""[{"iid":9,"web_url":"https://gitlab.example/octo/widgets/-/merge_requests/9","source_branch":"agent/issue-7","target_branch":"main","title":"Draft: Fix","description":"body","draft":true,"state":"opened","labels":[]}]"""));

        _ = await fixture.Provider.CreateIssueCommentAsync(Repository, 7, canonicalBody, CancellationToken.None);
        _ = await fixture.Provider.CreateDraftMergeRequestAsync(
            new CreateMergeRequestRequest(Repository, "agent/issue-7", "main", "Fix", "body", true, 7),
            CancellationToken.None);

        var restartedProvider = GitLabProviderFactory.Create(new GitLabProviderConfiguration(
            "gitlab", new Uri(fixture.Server.Url! + "/api/v4/"), null, ["gitlab.example"]));
        var checkpoint = Assert.Single(await CollectAsync(restartedProvider.GetIssueCommentsAsync(Repository, 7, CancellationToken.None)));
        var existingMergeRequest = await restartedProvider.FindMergeRequestAsync(Repository, "agent/issue-7", "main", CancellationToken.None);
        _ = await restartedProvider.UpdateIssueCommentAsync(Repository, 7, checkpoint.Id, canonicalBody + "\nreview ready", CancellationToken.None);

        Assert.Equal(100, checkpoint.Id);
        Assert.Equal(9, existingMergeRequest!.Number);
        Assert.Single(fixture.Server.LogEntries, entry => entry.RequestMessage!.Method == "POST" &&
            entry.RequestMessage.Path == "/api/v4/projects/123/issues/7/notes");
        Assert.Single(fixture.Server.LogEntries, entry => entry.RequestMessage!.Method == "POST" &&
            entry.RequestMessage.Path == "/api/v4/projects/123/merge_requests");
    }

    [Fact]
    public async Task DownloadAttachmentAsyncUsesAnonymousClientForSameAuthorityApiUrl()
    {
        var authenticated = new RecordingHttpMessageHandler();
        var anonymous = new RecordingHttpMessageHandler();
        var provider = new GitLabProvider(
            new GitLabApiClient(new HttpClient(), RetryPolicy.Default),
            new HttpClient(authenticated),
            new HttpClient(anonymous),
            ["gitlab.example"],
            string.Empty,
            "gitlab");
        var attachment = new ProviderAttachment(
            new Uri("https://gitlab.example/api/v4/projects/123/issues/7.png"),
            "7.png",
            null,
            new AttachmentSource("issue-description", "7"),
            false,
            new HashSet<IPAddress> { IPAddress.Loopback });

        _ = await provider.DownloadAttachmentAsync(
            attachment, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), 1024, CancellationToken.None);

        Assert.Equal(0, authenticated.RequestCount);
        Assert.Equal(1, anonymous.RequestCount);
    }

    [Fact]
    public void FactoryRejectsPlaintextBaseUriWhenCredentialsAreConfigured()
    {
        var configuration = new GitLabProviderConfiguration(
            "gitlab", new Uri("http://gitlab.example/api/v4/"), "secret-token", ["gitlab.example"]);

        var exception = Assert.Throws<ArgumentException>(() => GitLabProviderFactory.Create(configuration));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAttachmentAsyncRetriesTransientServerErrors()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/uploads/retry.pdf").UsingGet())
            .InScenario("gitlab-attachment-retry")
            .WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(503));
        fixture.Server
            .Given(Request.Create().WithPath("/uploads/retry.pdf").UsingGet())
            .InScenario("gitlab-attachment-retry")
            .WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("pdf-bytes"));

        var serverUri = new Uri(fixture.Server.Url!);
        var attachment = new ProviderAttachment(
            new Uri(fixture.Server.Url! + "/uploads/retry.pdf"),
            "retry.pdf",
            null,
            new AttachmentSource("issue-description", "7"),
            false,
            System.Net.Dns.GetHostAddresses(serverUri.Host).ToHashSet());

        var downloaded = await fixture.Provider.DownloadAttachmentAsync(
            attachment, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), 1024, CancellationToken.None);

        Assert.Equal(9, downloaded.SizeBytes);
        Assert.Equal(2, fixture.Server.LogEntries.Count(entry => entry.RequestMessage!.Path == "/uploads/retry.pdf"));
    }

    [Fact]
    public async Task DownloadAttachmentAsyncThrowsWhenContentExceedsLimit()
    {
        fixture.Server
            .Given(Request.Create().WithPath("/uploads/huge.zip").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/zip").WithBody(new string('a', 2000)));

        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var attachment = new ProviderAttachment(new Uri(fixture.Server.Url! + "/uploads/huge.zip"), "huge.zip", null, new AttachmentSource("issue-description", "7"), false, System.Net.Dns.GetHostAddresses(new Uri(fixture.Server.Url!).Host).ToHashSet());

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

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("attachment"),
            });
        }
    }
}
