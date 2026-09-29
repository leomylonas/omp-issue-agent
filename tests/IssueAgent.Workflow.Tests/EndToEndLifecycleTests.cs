using System.Diagnostics;
using IssueAgent.Configuration;
using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;
using IssueAgent.Providers.GitHub;
using IssueAgent.Providers.GitLab;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace IssueAgent.Workflow.Tests;

public sealed class EndToEndLifecycleTests : IDisposable
{
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-e2e-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public Task AssignmentThroughRepeatedConversationAndMergeCompletesWithoutDuplicateResources() =>
        RunLifecycleAsync(includeLfsAsset: false);

    [Fact]
    public Task AssignmentLifecycleMaterializesLfsContentBeforePublication() =>
        RunLifecycleAsync(includeLfsAsset: true);

    [Theory]
    [InlineData(ProviderKind.GitHub)]
    [InlineData(ProviderKind.GitLab)]
    public async Task ProviderBackedDiscoveryAndCanonicalStateMutationSurviveRestart(ProviderKind kind)
    {
        using var server = WireMockServer.Start();
        var repository = kind == ProviderKind.GitHub
            ? new RepositoryRef("github/octo/widgets", "octo", "widgets")
            : new RepositoryRef("123", "123", "");
        var canonicalBody = CanonicalCommentMarkdown.Render(new CanonicalCommentContent(
            "Validate titles.",
            ["Validation occurs at the boundary."],
            null,
            CanonicalStateSerializer.ToDocument(new WorkflowState(
                new WorkflowId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                WorkflowPhase.Planning,
                WorkflowOperationalState.Working,
                null,
                1,
                null,
                "session-1",
                "agent/1-title",
                "main",
                "base",
                DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
                null)));
        var provider = CreateProviderAndFixture(server, kind, canonicalBody);

        var discovered = new List<IssueSummary>();
        await foreach (var issue in provider.DiscoverAssignedOpenIssuesAsync(
            repository, "issue-agent", DateTimeOffset.MinValue, TestContext.Current.CancellationToken))
        {
            discovered.Add(issue);
        }

        Assert.Single(discovered);
        Assert.Equal(1, discovered[0].Number);

        var created = await provider.CreateIssueCommentAsync(
            repository,
            1,
            canonicalBody,
            TestContext.Current.CancellationToken);
        await provider.UpdateIssueCommentAsync(
            repository,
            1,
            created.Id,
            created.Body,
            TestContext.Current.CancellationToken);

        // A new provider instance represents a host restart. The persisted canonical comment is
        // returned by the provider fixture rather than by an in-process fake.
        var restartedProvider = CreateProvider(server, kind);
        var canonical = await CanonicalCommentLocator.FindAsync(
            restartedProvider, repository, 1, TestContext.Current.CancellationToken, "issue-agent");

        Assert.NotNull(canonical);
        Assert.Equal(created.Id, canonical.Id);
        Assert.Contains(CanonicalCommentMarkdown.StateLocatorMarker, canonical.Body, StringComparison.Ordinal);
    }

    private async Task RunLifecycleAsync(bool includeLfsAsset)
    {
        var repository = new RepositoryRef("test/octo/widgets", "octo", "widgets");
        var remoteRepository = CreateBareRemoteRepository(includeLfsAsset);
        var provider = new FakeGitProvider();
        var git = new LibGit2SharpRepositoryManager(Path.Combine(workspaceRoot, "repos"));
        var notifier = new RecordingNotifier();
        var dependencies = new WorkflowDependencies(provider, git, CreateContextBuilder(provider), notifier, clock);
        var config = CreateConfig(repository, remoteRepository);
        await git.EnsureBareRepositoryAsync(repository.Id, remoteRepository, config.GitAuthentication, CancellationToken.None);
        await git.FetchAsync(repository.Id, config.GitAuthentication, CancellationToken.None);
        provider.AddIssue(repository, 1, "Guard empty titles", "Saving an empty title fails.");

        var planningOmp = new FakeOmpClient()
            .EnqueueSessionId("session-1")
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Add validation.","decisions":["Validate at the boundary."]}"""));
        var planning = new PlanningWorkflow(dependencies);
        var planned = await planning.RunInitialPlanningAsync(config, 1, planningOmp, CancellationToken.None);

        provider.AddComment(repository, 1, "alice", "Also preserve whitespace-only titles.", clock.UtcNow.AddMinutes(1));
        provider.Labels[(repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowCommandLabels.Replan);
        var replanOmp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"planText":"Validate empty and whitespace-only titles.","decisions":["Normalize before validation."]}"""));
        var replanned = await planning.RunReplanAsync(config, 1, planned.State, replanOmp, CancellationToken.None);

        provider.Labels[(repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowCommandLabels.Implement);
        var implementationOmp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Added title validation.","keyChanges":["Normalized titles"],"decisions":["Validate normalized input"],"checksRun":["tests"],"knownFailures":[],"deviations":[],"risks":[]}"""));
        var implementation = await new ImplementationWorkflow(dependencies)
            .RunAsync(config, WorkflowMode.Full, 1, replanned.State, implementationOmp, CancellationToken.None);
        if (includeLfsAsset)
        {
            Assert.NotNull(expectedLfsAsset);
            Assert.Equal(
                expectedLfsAsset,
                File.ReadAllBytes(Path.Combine(workspaceRoot, implementation.State.WorkflowId.ToString(), "worktree", "asset.bin")));
        }

        provider.Labels[(repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowCommandLabels.Revise);
        provider.MergeRequestComments[(repository.Id, 1)] =
        [
            new ProviderComment(10, "reviewer", "Use the shared normalizer.", clock.UtcNow.AddMinutes(2), clock.UtcNow.AddMinutes(2), new AttachmentSource("merge-request-comment", "10"), false),
        ];
        var revisionOmp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Used the shared normalizer.","keyChanges":["Reused normalizer"],"decisions":[],"checksRun":["tests"],"knownFailures":[],"deviations":[],"risks":[]}"""));
        var revised = await new RevisionWorkflow(dependencies)
            .RunAsync(config, 1, implementation.State, revisionOmp, CancellationToken.None);

        provider.MergeRequests[1] = provider.MergeRequests[1] with { IsDraft = false, IsMerged = true, IsClosed = true };
        var canonical = Assert.Single(provider.IssueComments[(repository.Id, 1)], comment => comment.Body.Contains(CanonicalCommentMarkdown.StateLocatorMarker, StringComparison.Ordinal));
        var completed = await new WorkflowReconciliationService(dependencies)
            .ReconcileAsync(config, 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Completed, completed.Disposition);
        Assert.Equal(WorkflowPhase.Done, completed.State!.Phase);
        Assert.Equal(2, replanned.State.PlanRevision);
        Assert.Equal(WorkflowPhase.Review, revised.State.Phase);
        Assert.Single(provider.IssueComments[(repository.Id, 1)], comment => comment.Body.Contains(CanonicalCommentMarkdown.StateLocatorMarker, StringComparison.Ordinal));
        Assert.Single(provider.MergeRequests);
        Assert.Contains(implementation.State.Branch, RunGit(workspaceRoot, "ls-remote", remoteRepository, implementation.State.Branch), StringComparison.Ordinal);
        Assert.All(
            planningOmp.RunRequests.Concat(replanOmp.RunRequests).Concat(implementationOmp.RunRequests).Concat(revisionOmp.RunRequests),
            request => Assert.Equal("session-1", request.SessionId));
    }

    private static IGitProvider CreateProviderAndFixture(WireMockServer server, ProviderKind kind, string canonicalBody)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(canonicalBody);
        if (kind == ProviderKind.GitHub)
        {
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""[{"number":1,"title":"Guard titles","body":"Empty titles fail.","created_at":"2024-06-01T00:00:00Z","updated_at":"2024-06-01T00:00:00Z","state":"open","assignees":[{"login":"issue-agent"}],"labels":[]}]"""));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/comments").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(CommentJson(body, true, true)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/comments").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody(CommentJson(body, true, false)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/comments/10").UsingPatch()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(CommentJson(body, true, false)));
        }
        else
        {
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""[{"iid":1,"title":"Guard titles","description":"Empty titles fail.","created_at":"2024-06-01T00:00:00Z","updated_at":"2024-06-01T00:00:00Z","state":"opened","assignees":[{"username":"issue-agent"}],"labels":[]}]"""));
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues/1/notes").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(CommentJson(body, false, true)));
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues/1/notes").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody(CommentJson(body, false, false)));
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues/1/notes/10").UsingPut()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(CommentJson(body, false, false)));
        }

        return CreateProvider(server, kind);
    }
    private static string CommentJson(string body, bool gitHub, bool array) =>
        (array ? "[" : string.Empty) +
        """{"id":10,"body":""" + body +
        (gitHub
            ? ""","created_at":"2024-06-01T00:00:00Z","updated_at":"2024-06-01T00:00:00Z","user":{"login":"issue-agent","type":"Bot"}}"""
            : ""","created_at":"2024-06-01T00:00:00Z","updated_at":"2024-06-01T00:00:00Z","author":{"username":"issue-agent","bot":true}}""") +
        (array ? "]" : string.Empty);


    private static IGitProvider CreateProvider(WireMockServer server, ProviderKind kind) =>
        kind == ProviderKind.GitHub
            ? GitHubProviderFactory.Create(new GitHubProviderConfiguration("github", new Uri(server.Url! + "/"), null, ["github.example"]))
            : GitLabProviderFactory.Create(new GitLabProviderConfiguration("gitlab", new Uri(server.Url! + "/api/v4/"), null, ["gitlab.example"]));

    private static AgentContextBuilder CreateContextBuilder(FakeGitProvider provider) => new(
        provider,
        new AttachmentPipeline(provider, new AttachmentLimits()),
        new AgentContextBuilderOptions());

    private WorkflowRepositoryConfig CreateConfig(RepositoryRef repository, string remoteRepository) => new(
        repository,
        remoteRepository,
        workspaceRoot,
        "main",
        GitAuthentication.Anonymous(TlsTrust.System),
        new GitIdentity("IssueAgent", "issue-agent@example.com"),
        "/usr/local/bin/omp",
        [],
        new Dictionary<string, string>(),
        "plan",
        "task");

    private byte[]? expectedLfsAsset;

    private string CreateBareRemoteRepository(bool includeLfsAsset)
    {
        Directory.CreateDirectory(workspaceRoot);
        var remoteRepository = Path.Combine(workspaceRoot, "remote.git");
        var seedRepository = Path.Combine(workspaceRoot, "seed");
        RunGit(workspaceRoot, "init", "--bare", remoteRepository);
        RunGit(workspaceRoot, "clone", remoteRepository, seedRepository);
        RunGit(seedRepository, "config", "user.name", "IssueAgent Test");
        RunGit(seedRepository, "config", "user.email", "issue-agent-test@example.com");
        File.WriteAllText(Path.Combine(seedRepository, "README.md"), "# Test repository\n");
        if (includeLfsAsset)
        {
            RunGit(seedRepository, "lfs", "install", "--local");
            File.WriteAllText(Path.Combine(seedRepository, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
            expectedLfsAsset = new byte[4096];
            Random.Shared.NextBytes(expectedLfsAsset);
            File.WriteAllBytes(Path.Combine(seedRepository, "asset.bin"), expectedLfsAsset);
            RunGit(seedRepository, "add", ".gitattributes", "asset.bin");
        }

        RunGit(seedRepository, "add", "README.md");
        RunGit(seedRepository, "commit", "-m", "Initial commit");
        RunGit(seedRepository, "branch", "-M", "main");
        RunGit(seedRepository, "push", "--set-upstream", "origin", "main");
        Directory.Delete(seedRepository, recursive: true);
        return remoteRepository;
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {standardError}");
        }

        return standardOutput;
    }

    public void Dispose()
    {
        if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
    }
}
