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
        var head = await GitAsync(root, "--git-dir", remote, "rev-parse", "main");
        var canonical = CanonicalCommentMarkdown.Render(new CanonicalCommentContent(
            "Planning is in progress.", [], null,
            CanonicalStateSerializer.ToDocument(new WorkflowState(
                new WorkflowId(Guid.Parse("11111111-1111-1111-1111-111111111111")), WorkflowPhase.Planning,
                WorkflowOperationalState.Working, null, 0, null, string.Empty, "agent/1-guard-titles", "main", head,
                DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1)), null), null)));
        ConfigureProvider(server, kind, canonical);

        await using var first = CreateHost(kind, server, remote);
        using var workerCancellation = new CancellationTokenSource();
        var workers = first.Scheduler.RunWorkersAsync(workerCancellation.Token);
        await first.Scheduler.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(await first.Scheduler.WaitForDrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        workerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workers);

        // The WireMock assignment is an external boundary input. Both host instances route it
        // through classification without creating a second canonical comment.
        Assert.Contains(server.LogEntries, entry => entry.RequestMessage is { } request &&
            request.Method == "GET" &&
            (request.Path.Contains("issues", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase)));
        var createsBeforeRestart = server.LogEntries.Count(entry => entry.RequestMessage is { } request && request.Method == "POST" && (request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) || request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase)));

        await using var restarted = CreateHost(kind, server, remote);
        using var restartCancellation = new CancellationTokenSource();
        var restartWorkers = restarted.Scheduler.RunWorkersAsync(restartCancellation.Token);
        await restarted.Scheduler.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(await restarted.Scheduler.WaitForDrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        restartCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restartWorkers);

        Assert.Equal(createsBeforeRestart, server.LogEntries.Count(entry => entry.RequestMessage is { } request && request.Method == "POST" && (request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) || request.Path.Contains("notes", StringComparison.OrdinalIgnoreCase))));
    }

    private HostParts CreateHost(ProviderKind kind, WireMockServer server, string remote)
    {
        var provider = new ProviderOptions
        {
            Name = "provider", Kind = kind,
            BaseUri = new Uri(server.Url! + (kind == ProviderKind.GitLab ? "/api/v4/" : "/")),
            IdentityOverride = "issue-agent",
            Repositories = [new RepositoryOptions { Id = kind == ProviderKind.GitHub ? "octo/widgets" : "123", OwnerOrNamespace = kind == ProviderKind.GitHub ? "octo" : "123", Name = kind == ProviderKind.GitHub ? "widgets" : "", CloneUrl = remote, TargetBranch = "main" }]
        };
        var options = new IssueAgentOptions { Workspace = new WorkspaceOptions { RootPath = root }, Omp = new OmpOptions { ExecutablePath = "/bin/false" }, Concurrency = new ConcurrencyOptions { Agent = 1, Polling = 1 }, Retry = new RetryOptions { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, MaxJitter = TimeSpan.Zero }, Providers = [provider] };
        var effective = EffectiveConfigurationResolver.Resolve(options, _ => null, _ => throw new InvalidOperationException());
        var metrics = new IssueAgentMetrics();
        var registry = new ProviderRegistry(effective, options.Retry.ToPolicy(), metrics, NullLoggerFactory.Instance);
        var sessions = new ActiveOmpSessionRegistry(NullLogger<ActiveOmpSessionRegistry>.Instance);
        var dispatcher = new WorkflowDispatcher(Options.Create(options), effective, registry, new LibGit2SharpRepositoryManager(Path.Combine(root, "repos")), new OmpRuntimeEnvironmentFactory(effective), new FanOutNotifier([], options.Retry.ToPolicy(), new Dictionary<WorkflowNotificationKind, IReadOnlySet<string>>(), (_, _, _) => { }), sessions, new DefaultBranchResolver(registry), metrics, NullLogger<WorkflowDispatcher>.Instance, NullLogger<ObservableOmpClient>.Instance);
        var pool = new WorkflowWorkerPool(Options.Create(options), sessions, metrics, NullLogger<WorkflowWorkerPool>.Instance);
        return new(new PollingScheduler(Options.Create(options), effective, registry, dispatcher, pool, metrics, NullLogger<PollingScheduler>.Instance), pool, metrics);
    }
    private static void ConfigureProvider(WireMockServer server, ProviderKind kind, string canonical)
    {
        var comment = JsonSerializer.Serialize(canonical);
        if (kind == ProviderKind.GitHub)
        {
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues").UsingGet()).RespondWith(Response.Create().WithBody("[{\"number\":1,\"title\":\"Guard titles\",\"body\":\"Empty titles fail.\",\"created_at\":\"2024-06-01T00:00:00Z\",\"updated_at\":\"2024-06-01T00:00:00Z\",\"state\":\"open\",\"assignees\":[{\"login\":\"issue-agent\"}],\"labels\":[]}]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/comments").UsingGet()).RespondWith(Response.Create().WithBody($"[{{\"id\":10,\"body\":{comment},\"created_at\":\"2024-06-01T00:00:00Z\",\"updated_at\":\"2024-06-01T00:00:00Z\",\"user\":{{\"login\":\"issue-agent\",\"type\":\"Bot\"}}}}]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels").UsingGet()).RespondWith(Response.Create().WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/1/labels").UsingPut()).RespondWith(Response.Create().WithBody("[]"));
            server.Given(Request.Create().WithPath("/api/v3/repos/octo/widgets/issues/comments/10").UsingPatch()).RespondWith(Response.Create().WithBody($"{{\"id\":10,\"body\":{comment},\"user\":{{\"login\":\"issue-agent\",\"type\":\"Bot\"}}}}"));
        }
        else
        {
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues").UsingGet()).RespondWith(Response.Create().WithBody("[{\"iid\":1,\"title\":\"Guard titles\",\"description\":\"Empty titles fail.\",\"created_at\":\"2024-06-01T00:00:00Z\",\"updated_at\":\"2024-06-01T00:00:00Z\",\"state\":\"opened\",\"assignees\":[{\"username\":\"issue-agent\"}],\"labels\":[]}]"));
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues/1/notes").UsingGet()).RespondWith(Response.Create().WithBody($"[{{\"id\":10,\"body\":{comment},\"created_at\":\"2024-06-01T00:00:00Z\",\"updated_at\":\"2024-06-01T00:00:00Z\",\"author\":{{\"username\":\"issue-agent\",\"bot\":true}}}}]"));
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues/1").UsingPut()).RespondWith(Response.Create().WithBody("{}"));
            server.Given(Request.Create().WithPath("/api/v4/projects/123/issues/1/notes/10").UsingPut()).RespondWith(Response.Create().WithBody($"{{\"id\":10,\"body\":{comment},\"author\":{{\"username\":\"issue-agent\",\"bot\":true}}}}"));
        }
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
