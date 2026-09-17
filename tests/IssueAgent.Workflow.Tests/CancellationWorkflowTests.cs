using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class CancellationWorkflowTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-cancel-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task RunAsyncCancelsOmpSessionSetsCancelledPhaseAndCleansUpLocalState()
    {
        var state = SeedActiveState();
        var worktreePath = Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");
        Directory.CreateDirectory(worktreePath);
        var omp = new FakeOmpClient();

        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Cancelled, outcome.State.Phase);
        Assert.Equal(["session-1"], omp.CancelledSessionIds);
        Assert.False(Directory.Exists(worktreePath));
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:cancelled");
        Assert.DoesNotContain("agent:cmd:cancel", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Equal(WorkflowNotificationKind.Cancelled, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task RunAsyncSucceedsWithoutAnActiveOmpClient()
    {
        var state = SeedActiveState();
        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

        var outcome = await workflow.RunAsync(CreateConfig(), 1, state, omp: null, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Cancelled, outcome.State.Phase);
    }

    [Fact]
    public async Task CompleteOnMergeAsyncSetsDoneAndCleansUpLocalState()
    {
        var state = SeedActiveState();
        var worktreePath = Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");
        Directory.CreateDirectory(worktreePath);

        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.CompleteOnMergeAsync(CreateConfig(), 1, state, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Done, outcome.State.Phase);
        Assert.False(Directory.Exists(worktreePath));
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:done");
    }

    [Fact]
    public async Task CompleteOnCloseWithoutMergeAsyncSetsCancelledAndCleansUpLocalState()
    {
        var state = SeedActiveState();
        var worktreePath = Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");
        Directory.CreateDirectory(worktreePath);

        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.CompleteOnCloseWithoutMergeAsync(CreateConfig(), 1, state, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Cancelled, outcome.State.Phase);
        Assert.False(Directory.Exists(worktreePath));
    }

    private WorkflowState SeedActiveState()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var workflowId = WorkflowId.New();
        var state = new WorkflowState(
            workflowId, WorkflowPhase.Implementing, WorkflowOperationalState.Working, null,
            1, 1, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow.AddHours(-1));
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] = ["agent:phase:implementing", "agent:state:working", "agent:cmd:cancel"];
        return state;
    }

    private AgentContextBuilder CreateContextBuilder()
    {
        var attachmentPipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        return new AgentContextBuilder(provider, attachmentPipeline, new AgentContextBuilderOptions());
    }

    private WorkflowRepositoryConfig CreateConfig() => new(
        Repository,
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
        if (Directory.Exists(workspaceRoot))
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }
}
