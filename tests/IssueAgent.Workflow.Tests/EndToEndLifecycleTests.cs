using System.Diagnostics;
using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class EndToEndLifecycleTests : IDisposable
{
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-e2e-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("github/octo/widgets")]
    [InlineData("gitlab/octo/widgets")]
    public async Task AssignmentThroughRepeatedConversationAndMergeCompletesWithoutDuplicateResources(string repositoryId)
    {
        var repository = new RepositoryRef(repositoryId, "octo", "widgets");
        var remoteRepository = CreateBareRemoteRepository();
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

    private string CreateBareRemoteRepository()
    {
        Directory.CreateDirectory(workspaceRoot);
        var remoteRepository = Path.Combine(workspaceRoot, "remote.git");
        var seedRepository = Path.Combine(workspaceRoot, "seed");
        RunGit(workspaceRoot, "init", "--bare", remoteRepository);
        RunGit(workspaceRoot, "clone", remoteRepository, seedRepository);
        RunGit(seedRepository, "config", "user.name", "IssueAgent Test");
        RunGit(seedRepository, "config", "user.email", "issue-agent-test@example.com");
        File.WriteAllText(Path.Combine(seedRepository, "README.md"), "# Test repository\n");
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
