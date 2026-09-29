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

        await using var first = CreateHost(kind, server, remote);
        using var workerCancellation = new CancellationTokenSource();
        var workers = first.Scheduler.RunWorkersAsync(workerCancellation.Token);
        await first.Scheduler.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(await first.Scheduler.WaitForDrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        workerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workers);

        // The first host creates the durable workflow record through the production provider adapter.
        // The fixture retains comment mutations so the restarted host reads the record it created.
        Assert.Contains(server.LogEntries, entry => entry.RequestMessage is { } request &&
            request.Method == "GET" &&
            (request.Path.Contains("issues", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase)));
        var firstHostCanonicalCommentCreates = CountCanonicalCommentCreates(server);
        Assert.True(firstHostCanonicalCommentCreates == 1, DescribeRequests(server));
        Assert.True(fixture.HasPersistedComment);
        var persistedCommentReadsBeforeRestart = fixture.PersistedCommentReadCount;

        await using var restarted = CreateHost(kind, server, remote);
        using var restartCancellation = new CancellationTokenSource();
        var restartWorkers = restarted.Scheduler.RunWorkersAsync(restartCancellation.Token);
        await restarted.Scheduler.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(await restarted.Scheduler.WaitForDrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        restartCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restartWorkers);

        Assert.True(fixture.PersistedCommentReadCount > persistedCommentReadsBeforeRestart);
        Assert.Equal(0, CountCanonicalCommentCreates(server) - firstHostCanonicalCommentCreates);
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
        var dispatcher = new WorkflowDispatcher(Options.Create(options), effective, registry, new LibGit2SharpRepositoryManager(Path.Combine(root, "repos")), new OmpRuntimeEnvironmentFactory(effective), new FanOutNotifier([], options.Retry.ToPolicy(), new Dictionary<WorkflowNotificationKind, IReadOnlySet<string>>(), (_, _, _) => { }), sessions, new DefaultBranchResolver(registry), metrics, NullLogger<WorkflowDispatcher>.Instance, NullLogger<ObservableOmpClient>.Instance);
        var pool = new WorkflowWorkerPool(Options.Create(options), sessions, metrics, NullLogger<WorkflowWorkerPool>.Instance);
        return new(new PollingScheduler(Options.Create(options), effective, registry, dispatcher, pool, metrics, NullLogger<PollingScheduler>.Instance), pool, metrics);
    }
    private static ProviderFixtureState ConfigureProvider(WireMockServer server, ProviderKind kind)
    {
        var fixture = new ProviderFixtureState(kind);
        if (kind == ProviderKind.GitHub)
        {
            const string issue = """{"number":1,"title":"Guard titles","body":"Empty titles fail.","created_at":"2024-06-01T00:00:00Z","updated_at":"2024-06-01T00:00:00Z","state":"open","assignees":[{"login":"issue-agent"}],"labels":[]}""";
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody($"[{issue}]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(issue));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/comments").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(_ => fixture.CommentCollection()));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/comments").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels").UsingPut()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/comments/10").UsingPatch()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
        }
        else
        {
            const string issue = """{"iid":1,"title":"Guard titles","description":"Empty titles fail.","created_at":"2024-06-01T00:00:00Z","updated_at":"2024-06-01T00:00:00Z","state":"opened","assignees":[{"username":"issue-agent"}],"labels":[]}""";
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody($"[{issue}]"));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(issue));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1/notes").UsingGet()).RespondWith(Response.Create().WithBodyAsJson(_ => fixture.CommentCollection()));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1/notes").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1").UsingPut()).RespondWith(Response.Create().WithBody("{}"));
            server.Given(Request.Create().WithPath("/api/v4/projects/group/widgets/issues/1/notes/10").UsingPut()).RespondWith(Response.Create().WithBodyAsJson(request => fixture.PersistComment(request.Body!)));
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

    private sealed class ProviderFixtureState(ProviderKind kind)
    {
        private readonly object gate = new();
        private string? commentBody;
        private int persistedCommentReadCount;

        public bool HasPersistedComment
        {
            get
            {
                lock (gate)
                {
                    return commentBody is not null;
                }
            }
        }

        public int PersistedCommentReadCount
        {
            get
            {
                lock (gate)
                {
                    return persistedCommentReadCount;
                }
            }
        }


        public object CommentCollection()
        {
            lock (gate)
            {
                if (commentBody is null)
                {
                    return Array.Empty<object>();
                }

                persistedCommentReadCount++;
                return kind == ProviderKind.GitHub
                    ? new[] { new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", user = new { login = "issue-agent", type = "Bot" } } }
                    : new[] { new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", author = new { username = "issue-agent", bot = true } } };
            }
        }

        public object PersistComment(string requestBody)
        {
            using var document = JsonDocument.Parse(requestBody);
            var body = document.RootElement.GetProperty("body").GetString()
                ?? throw new InvalidOperationException("Provider comment mutation omitted its body.");

            lock (gate)
            {
                commentBody = body;
                return kind == ProviderKind.GitHub
                    ? new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", user = new { login = "issue-agent", type = "Bot" } }
                    : new { id = 10, body = commentBody, created_at = "2024-06-01T00:00:00Z", updated_at = "2024-06-01T00:00:00Z", author = new { username = "issue-agent", bot = true } };
            }
        }
    }

    private string CreatePlanningOmpExecutable()
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "deterministic-omp.py");
        File.WriteAllText(executable, """
            #!/usr/bin/env python3
            import json
            import os
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
                    send({"type": "message_update", "assistantMessageEvent": {"type": "text_delta", "delta": "{\"planText\":\"Validate titles at the boundary.\",\"decisions\":[\"Keep validation deterministic.\"]}"}})
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
