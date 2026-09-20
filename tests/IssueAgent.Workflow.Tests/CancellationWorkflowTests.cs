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
        var (state, content) = await SeedActiveStateAsync();
        var worktreePath = Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");
        Directory.CreateDirectory(worktreePath);
        var attachmentsPath = Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "attachments");
        Directory.CreateDirectory(attachmentsPath);
        await File.WriteAllTextAsync(Path.Combine(attachmentsPath, "evidence.txt"), "evidence", TestContext.Current.CancellationToken);
        var omp = new FakeOmpClient();

        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.RunAsync(CreateConfig(), 1, state, content, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Cancelled, outcome.State.Phase);
        Assert.Equal(["session-1"], omp.CancelledSessionIds);
        Assert.False(Directory.Exists(worktreePath));
        Assert.False(Directory.Exists(Path.Combine(workspaceRoot, state.WorkflowId.ToString())));
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:cancelled");
        Assert.DoesNotContain("agent:cmd:cancel", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain(WorkflowLabels.WorkingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain(WorkflowLabels.WaitingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Equal(WorkflowNotificationKind.Cancelled, Assert.Single(notifier.Notifications).Kind);
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("cancelled", persisted.State.Phase);
    }

    [Fact]
    public async Task RunAsyncSucceedsWithoutAnActiveOmpClient()
    {
        var (state, content) = await SeedActiveStateAsync();
        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

        var outcome = await workflow.RunAsync(CreateConfig(), 1, state, content, omp: null, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Cancelled, outcome.State.Phase);
    }

    [Fact]
    public async Task CompleteOnMergeAsyncPersistsDoneStateBeforeCleaningUpLocalState()
    {
        var (state, content) = await SeedActiveStateAsync();
        var worktreePath = Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");
        Directory.CreateDirectory(worktreePath);

        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.CompleteOnMergeAsync(CreateConfig(), 1, state, content, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Done, outcome.State.Phase);
        Assert.False(Directory.Exists(worktreePath));
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:done");
        Assert.DoesNotContain(WorkflowLabels.WorkingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain(WorkflowLabels.WaitingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("done", persisted.State.Phase);
    }

    [Fact]
    public async Task CompleteOnCloseWithoutMergeAsyncPersistsCancelledStateBeforeCleaningUpLocalState()
    {
        var (state, content) = await SeedActiveStateAsync();
        var worktreePath = Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");
        Directory.CreateDirectory(worktreePath);

        var workflow = new CancellationWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.CompleteOnCloseWithoutMergeAsync(CreateConfig(), 1, state, content, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Cancelled, outcome.State.Phase);
        Assert.False(Directory.Exists(worktreePath));
        Assert.DoesNotContain(WorkflowLabels.WorkingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain(WorkflowLabels.WaitingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("cancelled", persisted.State.Phase);
    }

    private async Task<(WorkflowState State, CanonicalCommentContent Content)> SeedActiveStateAsync()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var workflowId = WorkflowId.New();
        var state = new WorkflowState(
            workflowId, WorkflowPhase.Implementing, WorkflowOperationalState.Working, null,
            1, 1, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow.AddHours(-1));
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] = ["agent:phase:implementing", "agent:state:working", "agent:cmd:cancel"];
        var content = new CanonicalCommentContent("Plan text.", ["Decision."], "Implementation summary.", CanonicalStateSerializer.ToDocument(state, null));
        await provider.CreateIssueCommentAsync(Repository, 1, CanonicalCommentMarkdown.Render(content), CancellationToken.None);
        return (state, content);
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
