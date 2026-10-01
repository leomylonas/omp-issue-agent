using System.Diagnostics;
using System.Text.Json;
using IssueAgent.Configuration;
using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Host;
using IssueAgent.Notifications;
using IssueAgent.Observability;
using IssueAgent.Omp;
using IssueAgent.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;
using WireMock.Server;

namespace IssueAgent.Host.Tests;

public sealed class ProviderSchedulerLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "issue-agent-host-e2e", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ProviderKind.GitHub)]
    [InlineData(ProviderKind.GitLab)]
    public async Task AssignedIssueIsDispatchedThroughHostAndRestartDoesNotCreateAnotherCanonicalComment(ProviderKind kind)
    {
        using var server = WireMockServer.Start();
        var remote = await CreateRemoteAsync();
        var fixture = ConfigureProvider(server, kind);

        await RunPollAsync(kind, server, remote);

        Assert.Contains(server.LogEntries, entry => entry.RequestMessage is { } request &&
            request.Method == "GET" &&
            (request.Path.Contains("issues", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase)));
        var firstHostCanonicalCommentCreates = CountCanonicalCommentCreates(server);
        Assert.True(firstHostCanonicalCommentCreates == 1, DescribeRequests(server));
        Assert.True(fixture.HasPersistedComment);
        var persistedCommentReadsBeforeRestart = fixture.PersistedCommentReadCount;

        await RunPollAsync(kind, server, remote);

        Assert.True(fixture.PersistedCommentReadCount > persistedCommentReadsBeforeRestart);
        Assert.Equal(0, CountCanonicalCommentCreates(server) - firstHostCanonicalCommentCreates);
    }

    [Theory]
    [InlineData(ProviderKind.GitHub)]
    [InlineData(ProviderKind.GitLab)]
    public async Task AssignedIssueCompletesPlanReplanImplementReviseAndMergeThroughProductionProviders(ProviderKind kind)
    {
        using var server = WireMockServer.Start();
        var remote = await CreateRemoteAsync();
        var fixture = ConfigureProvider(server, kind);

        await RunPollAsync(kind, server, remote);
        Assert.Equal(WorkflowPhase.Planned, fixture.Phase);
        Assert.Equal(1, fixture.PlanRevision);
        Assert.Contains(WorkflowLabels.PlannedPhase, fixture.IssueLabelSnapshot);
        Assert.Contains(WorkflowLabels.WaitingState, fixture.IssueLabelSnapshot);

        fixture.AddIssueLabel(WorkflowCommandLabels.Replan);
        await RunPollAsync(kind, server, remote);
        Assert.Equal(WorkflowPhase.Planned, fixture.Phase);
        Assert.Equal(2, fixture.PlanRevision);
        Assert.DoesNotContain(WorkflowCommandLabels.Replan, fixture.IssueLabelSnapshot);

        fixture.AddIssueLabel(WorkflowCommandLabels.Implement);
        await RunPollAsync(kind, server, remote);
        Assert.True(fixture.HasPersistedMergeRequest);
        Assert.Equal(1, fixture.MergeRequestCreateCount);
        Assert.Equal(WorkflowPhase.Review, fixture.Phase);
        Assert.Contains(WorkflowLabels.ReviewPhase, fixture.IssueLabelSnapshot);
        Assert.Contains(WorkflowLabels.WaitingState, fixture.IssueLabelSnapshot);
        Assert.DoesNotContain(WorkflowCommandLabels.Implement, fixture.IssueLabelSnapshot);

        fixture.AddMergeRequestLabel(WorkflowCommandLabels.Revise);
        await RunPollAsync(kind, server, remote);
        Assert.Equal(WorkflowPhase.Review, fixture.Phase);
        Assert.DoesNotContain(WorkflowCommandLabels.Revise, fixture.MergeRequestLabelSnapshot);

        fixture.MarkMergeRequestMerged();
        await RunPollAsync(kind, server, remote);
        Assert.Equal(WorkflowPhase.Done, fixture.Phase);
        Assert.Contains(WorkflowLabels.DonePhase, fixture.IssueLabelSnapshot);
        Assert.DoesNotContain(WorkflowLabels.WaitingState, fixture.IssueLabelSnapshot);

        var canonicalCommentCreates = CountCanonicalCommentCreates(server);
        var mergeRequestCreates = fixture.MergeRequestCreateCount;
        await RunPollAsync(kind, server, remote);
        Assert.Equal(canonicalCommentCreates, CountCanonicalCommentCreates(server));
        Assert.Equal(mergeRequestCreates, fixture.MergeRequestCreateCount);
    }


    [Theory]
    [InlineData(ProviderKind.GitHub)]
    [InlineData(ProviderKind.GitLab)]
    public async Task MalformedProviderDiscoveryInputDoesNotDispatchOrCreateCanonicalState(ProviderKind kind)
    {
        using var server = WireMockServer.Start();
        var path = kind == ProviderKind.GitHub
            ? "/api/v3/repos/octo/widgets/issues"
            : "/api/v4/projects/group/widgets/issues";
        server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("{ invalid-json"));

        await using var host = CreateHost(kind, server, await CreateRemoteAsync());

        await host.Scheduler.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(server.LogEntries, entry => entry.RequestMessage is { } request &&
            request.Method == "POST" &&
            (request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(ProviderKind.GitHub)]
    [InlineData(ProviderKind.GitLab)]
    public async Task ProviderDiscoveryFailureDoesNotDispatchOrCreateCanonicalState(ProviderKind kind)
    {
        using var server = WireMockServer.Start();
        var path = kind == ProviderKind.GitHub
            ? "/api/v3/repos/octo/widgets/issues"
            : "/api/v4/projects/group/widgets/issues";
        server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        await using var host = CreateHost(kind, server, await CreateRemoteAsync());

        await host.Scheduler.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(server.LogEntries, entry => entry.RequestMessage is { } request &&
            request.Method == "POST" &&
            (request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task RunPollAsync(ProviderKind kind, WireMockServer server, string remote)
    {
        await using var host = CreateHost(kind, server, remote);
        using var workerCancellation = new CancellationTokenSource();
        var workers = host.Scheduler.RunWorkersAsync(workerCancellation.Token);
        for (var poll = 0; poll < 2; poll++)
        {
            await host.Scheduler.PollOnceAsync(TestContext.Current.CancellationToken);
            Assert.True(await host.Scheduler.WaitForDrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        workerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workers);
    }

    private HostParts CreateHost(ProviderKind kind, WireMockServer server, string remote)
    {
        var isGitHub = kind == ProviderKind.GitHub;
        var provider = new ProviderOptions
        {
            Name = "provider", Kind = kind,
            BaseUri = new Uri(server.Url! + (isGitHub ? "/" : "/api/v4/")),
            IdentityOverride = "issue-agent",
            Repositories =
            [
                isGitHub
                    ? new RepositoryOptions { Id = "octo/widgets", OwnerOrNamespace = "octo", Name = "widgets", CloneUrl = remote, TargetBranch = "main" }
                    : new RepositoryOptions { Id = "group/widgets", OwnerOrNamespace = "group", Name = "widgets", CloneUrl = remote, TargetBranch = "main" },
            ],
        };
        var ompExecutable = CreatePlanningOmpExecutable();
        var options = new IssueAgentOptions { Workspace = new WorkspaceOptions { RootPath = root }, Omp = new OmpOptions { ExecutablePath = ompExecutable }, Concurrency = new ConcurrencyOptions { Agent = 1, Polling = 1 }, Retry = new RetryOptions { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, MaxJitter = TimeSpan.Zero }, Providers = [provider] };
        var effective = EffectiveConfigurationResolver.Resolve(options, _ => null, _ => throw new InvalidOperationException());
        var metrics = new IssueAgentMetrics();
        var registry = new ProviderRegistry(effective, options.Retry.ToPolicy(), metrics, NullLoggerFactory.Instance);
        var sessions = new ActiveOmpSessionRegistry(NullLogger<ActiveOmpSessionRegistry>.Instance);
        var pollingEligibility = new PollingEligibilitySchedule();
        var dispatcher = new WorkflowDispatcher(Options.Create(options), effective, registry, new LibGit2SharpRepositoryManager(Path.Combine(root, "repos")), new OmpRuntimeEnvironmentFactory(effective), new FanOutNotifier([], options.Retry.ToPolicy(), new Dictionary<WorkflowNotificationKind, IReadOnlySet<string>>(), (_, _, _) => { }), sessions, new DefaultBranchResolver(registry), metrics, NullLogger<WorkflowDispatcher>.Instance, NullLogger<ObservableOmpClient>.Instance);
        var pool = new WorkflowWorkerPool(Options.Create(options), sessions, pollingEligibility, metrics, NullLogger<WorkflowWorkerPool>.Instance);
        return new(new PollingScheduler(Options.Create(options), effective, registry, dispatcher, pool, pollingEligibility, metrics, NullLogger<PollingScheduler>.Instance), pool, metrics);
    }
    private static ProviderFixtureState ConfigureProvider(WireMockServer server, ProviderKind kind)
    {
        var fixture = new ProviderFixtureState(kind);
        if (kind == ProviderKind.GitHub)
        {
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => new[] { fixture.Issue() }));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.Issue()));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/comments").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.CommentCollection()));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/comments").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.IssueLabels()));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels").UsingPut()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(request => fixture.SetIssueLabels(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(request => fixture.AddIssueLabels(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels/*").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204).WithBodyAsJson(request => fixture.RemoveIssueLabel(request.Path!)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/labels/*").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"name":"managed"}"""));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/labels").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody("""{"name":"managed"}"""));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/comments/10").UsingPatch()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.MergeRequestCollection()));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBodyAsJson(request => fixture.CreateMergeRequest(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls/1").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.MergeRequest()));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls/1/comments").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/timeline").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls/1/reviews").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/graphql").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}"""));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1").UsingPatch()).RespondWith(Response.Create().WithBody("{}"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls/1/labels").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.MergeRequestLabels()));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls/1/labels").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/pulls/1/labels/*").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204).WithBodyAsJson(request => fixture.RemoveMergeRequestLabel(request.Path!)));
        }
        else
        {
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => new[] { fixture.Issue() }));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.Issue()));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1/notes").UsingGet()).RespondWith(Response.Create().WithBodyAsJson(_ => fixture.CommentCollection()));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1/notes").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1").UsingPut()).RespondWith(Response.Create().WithBodyAsJson(request => fixture.UpdateGitLabIssue(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1/notes/10").UsingPut()).RespondWith(Response.Create().WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/merge_requests").UsingGet()).RespondWith(Response.Create().WithBodyAsJson(_ => fixture.MergeRequestCollection()));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/merge_requests").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(request => fixture.CreateMergeRequest(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/merge_requests/1").UsingGet()).RespondWith(Response.Create().WithBodyAsJson(_ => fixture.MergeRequest()));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/merge_requests/1").UsingPut()).RespondWith(Response.Create().WithBodyAsJson(request => fixture.UpdateGitLabMergeRequest(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/merge_requests/1/notes").UsingGet()).RespondWith(Response.Create().WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/merge_requests/1/discussions").UsingGet()).RespondWith(Response.Create().WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1/links").UsingGet()).RespondWith(Response.Create().WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets").UsingGet()).RespondWith(Response.Create().WithBody("""{"id":1,"default_branch":"main"}"""));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/labels").UsingGet()).RespondWith(Response.Create().WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/labels").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithBody("""{"name":"managed"}"""));
        }

        return fixture;
    }

    private static int CountCanonicalCommentCreates(WireMockServer server) =>
        server.LogEntries.Count(entry => entry.RequestMessage is { } request &&
            request.Method == "POST" &&
            (request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase)) &&
            request.Body is { } requestBody &&
            IsCanonicalCommentMutation(requestBody));

    private static string DescribeRequests(WireMockServer server) =>
        string.Join(Environment.NewLine, server.LogEntries.Select(entry =>
            $"{entry.RequestMessage?.Method} {entry.RequestMessage?.Path} {entry.RequestMessage?.Body}"));

    private static bool IsCanonicalCommentMutation(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement.TryGetProperty("body", out var body) &&
            body.GetString() is { } markdown &&
            CanonicalCommentMarkdown.IsCanonicalComment(markdown);
    }

    private sealed record ProviderLabel(string name);

    private sealed class ProviderFixtureState(ProviderKind kind)
    {
        private readonly object gate = new();
        private readonly HashSet<string> issueLabels = [];
        private readonly HashSet<string> mergeRequestLabels = [];
        private string? commentBody;
        private string? sourceBranch;
        private string? mergeRequestBody;
        private int persistedCommentReadCount;
        private int mergeRequestCreateCount;
        private bool mergeRequestMerged;

        public bool HasPersistedComment { get { lock (gate) return commentBody is not null; } }
        public bool HasPersistedMergeRequest { get { lock (gate) return sourceBranch is not null; } }
        public int PersistedCommentReadCount { get { lock (gate) return persistedCommentReadCount; } }
        public int MergeRequestCreateCount { get { lock (gate) return mergeRequestCreateCount; } }
        public string CanonicalBody { get { lock (gate) return commentBody ?? string.Empty; } }
        public string SourceBranch { get { lock (gate) return sourceBranch ?? throw new InvalidOperationException("Merge request has not been created."); } }
        public int PlanRevision { get { lock (gate) return ParseState().PlanRevision; } }
        public WorkflowPhase Phase { get { lock (gate) return ParseState().Phase; } }
        public IReadOnlySet<string> IssueLabelSnapshot { get { lock (gate) return issueLabels.ToHashSet(StringComparer.Ordinal); } }
        public IReadOnlySet<string> MergeRequestLabelSnapshot { get { lock (gate) return (kind == ProviderKind.GitHub ? issueLabels : mergeRequestLabels).ToHashSet(StringComparer.Ordinal); } }

        public void AddIssueLabel(string label) { lock (gate) issueLabels.Add(label); }
        public void AddMergeRequestLabel(string label) { lock (gate) (kind == ProviderKind.GitHub ? issueLabels : mergeRequestLabels).Add(label); }
        public void MarkMergeRequestMerged() { lock (gate) mergeRequestMerged = true; }
        public object Issue()
        {
            lock (gate)
            {
                return kind == ProviderKind.GitHub
                    ? new { number = 1, title = "Guard titles", body = "Empty titles fail.", created_at = "2024-06-01T00:00:00Z", updated_at = DateTimeOffset.UtcNow.ToString("O"), state = "open", assignees = new[] { new { login = "issue-agent" } }, labels = issueLabels.Select(label => new ProviderLabel(label)).ToArray() }
                    : new { iid = 1, title = "Guard titles", description = "Empty titles fail.", created_at = "2024-06-01T00:00:00Z", updated_at = DateTimeOffset.UtcNow.ToString("O"), state = "opened", assignees = new[] { new { username = "issue-agent" } }, labels = issueLabels.ToArray() };
            }
        }
        public ProviderLabel[] IssueLabels() { lock (gate) return issueLabels.Select(label => new ProviderLabel(label)).ToArray(); }
        public ProviderLabel[] MergeRequestLabels() { lock (gate) return (kind == ProviderKind.GitHub ? issueLabels : mergeRequestLabels).Select(label => new ProviderLabel(label)).ToArray(); }
        public ProviderLabel[] RemoveIssueLabel(string path)
        {
            lock (gate)
            {
                issueLabels.Remove(LabelFromPath(path));
                return IssueLabels();
            }
        }
        public ProviderLabel[] RemoveMergeRequestLabel(string path)
        {
            lock (gate)
            {
                (kind == ProviderKind.GitHub ? issueLabels : mergeRequestLabels).Remove(LabelFromPath(path));
                return MergeRequestLabels();
            }
        }
        public ProviderLabel[] SetIssueLabels(string requestBody)
        {
            lock (gate)
            {
                issueLabels.Clear();
                foreach (var label in JsonSerializer.Deserialize<string[]>(requestBody) ?? []) issueLabels.Add(label);
                return IssueLabels();
            }
        }
        public ProviderLabel[] AddIssueLabels(string requestBody)
        {
            using var document = JsonDocument.Parse(requestBody);
            lock (gate)
            {
                issueLabels.RemoveWhere(label => label.StartsWith("agent:phase:", StringComparison.Ordinal) ||
                    label.StartsWith("agent:state:", StringComparison.Ordinal));
                foreach (var label in document.RootElement.GetProperty("labels").EnumerateArray()) issueLabels.Add(label.GetString()!);
                return IssueLabels();
            }
        }
        public object UpdateGitLabIssue(string requestBody)
        {
            using var document = JsonDocument.Parse(requestBody);
            lock (gate)
            {
                UpdateGitLabLabels(document.RootElement, issueLabels, removeManagedLabels: true);
                return Issue();
            }
        }
        public object UpdateGitLabMergeRequest(string requestBody)
        {
            using var document = JsonDocument.Parse(requestBody);
            lock (gate)
            {
                UpdateGitLabLabels(document.RootElement, mergeRequestLabels, removeManagedLabels: false);
                return MergeRequest();
            }
        }
        public object CommentCollection()
        {
            lock (gate)
            {
                if (commentBody is null) return Array.Empty<object>();
                persistedCommentReadCount++;
                return kind == ProviderKind.GitHub
                    ? new[] { new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", user = new { login = "issue-agent", type = "Bot" } } }
                    : new[] { new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", author = new { username = "issue-agent", bot = true } } };
            }
        }
        public object PersistComment(string requestBody)
        {
            using var document = JsonDocument.Parse(requestBody);
            var body = document.RootElement.GetProperty("body").GetString() ?? throw new InvalidOperationException("Provider comment mutation omitted its body.");
            lock (gate)
            {
                commentBody = body;
                return kind == ProviderKind.GitHub
                    ? new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", user = new { login = "issue-agent", type = "Bot" } }
                    : new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", author = new { username = "issue-agent", bot = true } };
            }
        }
        public object[] MergeRequestCollection() { lock (gate) return sourceBranch is null ? [] : [MergeRequest()]; }
        public object CreateMergeRequest(string requestBody)
        {
            using var document = JsonDocument.Parse(requestBody);
            lock (gate)
            {
                sourceBranch = document.RootElement.GetProperty(kind == ProviderKind.GitHub ? "head" : "source_branch").GetString();
                mergeRequestBody = document.RootElement.GetProperty(kind == ProviderKind.GitHub ? "body" : "description").GetString();
                mergeRequestCreateCount++;
                return MergeRequest();
            }
        }
        public object MergeRequest()
        {
            lock (gate)
            {
                var branch = sourceBranch ?? "agent/1-guard-titles";
                return kind == ProviderKind.GitHub
                    ? new { number = 1, head = new { @ref = branch }, @base = new { @ref = "main" }, title = "Guard titles", body = mergeRequestBody ?? "Implementation", draft = true, merged = mergeRequestMerged, merged_at = mergeRequestMerged ? "2024-06-02T00:00:00Z" : null, state = mergeRequestMerged ? "closed" : "open", html_url = "https://example.test/pull/1" }
                    : new { iid = 1, source_branch = branch, target_branch = "main", title = "Draft: Guard titles", description = mergeRequestBody ?? "Implementation", draft = true, state = mergeRequestMerged ? "merged" : "opened", labels = mergeRequestLabels.ToArray(), source_project_id = 1L, web_url = "https://example.test/merge_requests/1" };
            }
        }
        private static string LabelFromPath(string path) => Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);

        private static void UpdateGitLabLabels(JsonElement request, HashSet<string> labels, bool removeManagedLabels)
        {
            if (request.TryGetProperty("add_labels", out var added) && added.GetString() is { } addedValue)
            {
                if (removeManagedLabels)
                {
                    labels.RemoveWhere(label => label.StartsWith("agent:phase:", StringComparison.Ordinal) ||
                        label.StartsWith("agent:state:", StringComparison.Ordinal));
                }
                foreach (var label in addedValue.Split(',', StringSplitOptions.RemoveEmptyEntries)) labels.Add(label);
            }
            if (request.TryGetProperty("remove_labels", out var removed) && removed.GetString() is { } removedValue)
            {
                foreach (var label in removedValue.Split(',', StringSplitOptions.RemoveEmptyEntries)) labels.Remove(label);
            }
        }

        private WorkflowState ParseState() => commentBody is null
            ? throw new InvalidOperationException("Canonical comment has not been persisted.")
            : CanonicalStateSerializer.ToWorkflowState(CanonicalCommentMarkdown.Parse(commentBody).State);
    }

    private string CreatePlanningOmpExecutable()
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "deterministic-omp.py");
        File.WriteAllText(executable, """
            #!/usr/bin/python3
            import json
            import os
            import subprocess
            import sys

            session_dir = sys.argv[sys.argv.index("--session-dir") + 1]
            session_file = os.path.join(session_dir, "session.jsonl")
            session_id = "lifecycle-session"

            def send(value):
                print(json.dumps(value), flush=True)

            def response(request, data):
                send({"type": "response", "id": request["id"], "command": request["type"], "success": True, "data": data})

            os.makedirs(session_dir, exist_ok=True)
            open(session_file, "a").close()
            send({"type": "ready", "protocolVersion": 1, "supportedProtocolVersions": [1]})
            for line in sys.stdin:
                request = json.loads(line)
                command = request["type"]
                if command == "new_session":
                    response(request, {"cancelled": False})
                elif command == "switch_session":
                    response(request, {"cancelled": False})
                elif command == "get_state":
                    response(request, {"sessionId": session_id, "sessionFile": session_file, "model": {"provider": "configured", "id": "plan"}})
                elif command == "set_model":
                    response(request, {"provider": request["provider"], "id": request["modelId"]})
                elif command == "prompt":
                    response(request, {"agentInvoked": True})
                    prompt = request.get("message", "").lower()
                    if "the plan below has been approved" in prompt or "the human has reviewed" in prompt:
                        with open("Lifecycle.txt", "a") as lifecycle:
                            lifecycle.write("validated\\n")
                        subprocess.run(["/usr/bin/git", "add", "Lifecycle.txt"], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                        subprocess.run(["/usr/bin/git", "-c", "user.name=IssueAgent", "-c", "user.email=issue-agent@example.com", "commit", "-m", "validate titles"], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                        result = "{\"summary\":\"Validated titles.\",\"keyChanges\":[\"Added lifecycle validation\"],\"decisions\":[\"Keep validation deterministic.\"],\"checksRun\":[\"deterministic smoke\"],\"knownFailures\":[],\"deviations\":[],\"risks\":[]}"
                    elif "requested changes to the existing plan" in prompt:
                        result = "{\"planText\":\"Validate empty and whitespace-only titles.\",\"decisions\":[\"Normalize before validation.\"]}"
                    else:
                        result = "{\"planText\":\"Validate titles at the boundary.\",\"decisions\":[\"Keep validation deterministic.\"]}"
                    send({"type": "message_update", "assistantMessageEvent": {"type": "text_delta", "delta": result}})
                    send({"type": "agent_end", "messages": [], "isTerminal": True})
                elif command == "abort":
                    response(request, {"cancelled": True})
            """);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The deterministic OMP test server requires a Unix executable bit.");
        }
        File.SetUnixFileMode(executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executable;
    }

    private async Task<string> CreateRemoteAsync()
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        await GitAsync(root, "init", "--bare", remote);
        var work = Path.Combine(root, "seed");
        await GitAsync(root, "clone", remote, work);
        await GitAsync(work, "checkout", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "seed");
        await GitAsync(work, "add", ".");
        await GitAsync(work, "-c", "user.name=IssueAgent", "-c", "user.email=issue-agent@example.com", "commit", "-m", "seed");
        await GitAsync(work, "push", "origin", "main");
        return remote;
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
        return output.Trim();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed record HostParts(PollingScheduler Scheduler, WorkflowWorkerPool Pool, IssueAgentMetrics Metrics) : IAsyncDisposable { public ValueTask DisposeAsync() { Pool.Dispose(); Metrics.Dispose(); return ValueTask.CompletedTask; } }
}
