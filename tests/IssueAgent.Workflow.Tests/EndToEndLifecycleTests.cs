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
        var provider = new FakeGitProvider();
        var git = new FakeGitRepositoryManager { RemoteBranchCommitToReturn = null };
        var notifier = new RecordingNotifier();
        var dependencies = new WorkflowDependencies(provider, git, CreateContextBuilder(provider), notifier, clock);
        var config = CreateConfig(repository);
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
        git.RemoteBranchCommitToReturn = git.BranchCommitToReturn;
        var canonical = Assert.Single(provider.IssueComments[(repository.Id, 1)], comment => comment.Body.Contains(CanonicalCommentMarkdown.StateLocatorMarker, StringComparison.Ordinal));
        var completed = await new WorkflowReconciliationService(dependencies)
            .ReconcileAsync(config, 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Completed, completed.Disposition);
        Assert.Equal(WorkflowPhase.Done, completed.State!.Phase);
        Assert.Equal(2, replanned.State.PlanRevision);
        Assert.Equal(WorkflowPhase.Review, revised.State.Phase);
        Assert.Single(provider.IssueComments[(repository.Id, 1)], comment => comment.Body.Contains(CanonicalCommentMarkdown.StateLocatorMarker, StringComparison.Ordinal));
        Assert.Single(provider.MergeRequests);
        Assert.All(
            planningOmp.RunRequests.Concat(replanOmp.RunRequests).Concat(implementationOmp.RunRequests).Concat(revisionOmp.RunRequests),
            request => Assert.Equal("session-1", request.SessionId));
    }

    private static AgentContextBuilder CreateContextBuilder(FakeGitProvider provider) => new(
        provider,
        new AttachmentPipeline(provider, new AttachmentLimits()),
        new AgentContextBuilderOptions());

    private WorkflowRepositoryConfig CreateConfig(RepositoryRef repository) => new(
        repository,
        Path.Combine(workspaceRoot, "repo"),
        workspaceRoot,
        "main",
        GitAuthentication.Anonymous(TlsTrust.System),
        new GitIdentity("IssueAgent", "issue-agent@example.com"),
        "/usr/local/bin/omp",
        [],
        new Dictionary<string, string>(),
        "plan",
        "task");

    public void Dispose()
    {
        if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
    }
}
