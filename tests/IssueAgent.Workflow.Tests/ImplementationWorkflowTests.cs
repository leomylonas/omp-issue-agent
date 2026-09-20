using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class ImplementationWorkflowTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-impl-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task RunAsyncInPlanOnlyModeRejectsImplementCommandAndRestoresWaiting()
    {
        var state = await SeedApprovedPlanAsync();
        var workflow = CreateWorkflow();

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.PlanOnly, 1, state, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WaitingReason.PlanApproval, outcome.State.WaitingReason);
        Assert.Empty(provider.UpdatedComments);
    }

    [Fact]
    public async Task RunAsyncPublishesDraftMergeRequestAndSetsReviewWaiting()
    {
        var state = await SeedApprovedPlanAsync();
        var reviewFeedbackSnapshotCaptured = false;
        provider.OnReviewThreadsEnumeration = () => reviewFeedbackSnapshotCaptured = true;
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Added a guard clause.","keyChanges":["Guard clause in Save()"],"decisions":["Chose guard clause for clarity."],"checksRun":["dotnet test"],"knownFailures":[],"deviations":[],"risks":[]}
            """));

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(WaitingReason.ReviewRequested, outcome.State.WaitingReason);
        Assert.Single(provider.MergeRequests);
        Assert.True(provider.MergeRequests[1].IsDraft);
        Assert.Contains("<!-- issue-agent:workflow:", provider.MergeRequests[1].Description, StringComparison.Ordinal);
        Assert.Contains("Fixes #1", provider.MergeRequests[1].Description, StringComparison.Ordinal);
        var updated = provider.UpdatedComments[^1];
        Assert.Contains("Added a guard clause.", updated.Body, StringComparison.Ordinal);
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:review");
        Assert.Equal(WorkflowNotificationKind.ImplementationReady, Assert.Single(notifier.Notifications).Kind);
        Assert.True(reviewFeedbackSnapshotCaptured);
    }

    [Fact]
    public async Task RunAsyncPersistsFailureWhenOmpResultViolatesContract()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient().EnqueueRun(
            new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Missing required fields."}"""));

        var outcome = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("failed", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State.Phase);
        Assert.Equal(WorkflowNotificationKind.ImplementationFailed, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task RunAsyncOmitsIssueClosingReferenceWhenDisabled()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Added a guard clause.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}
            """));

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig() with { CloseIssueOnMerge = false },
            WorkflowMode.Full,
            1,
            state,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.DoesNotContain("Fixes #1", provider.MergeRequests[1].Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Closes #1", provider.MergeRequests[1].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncPausesForMaterialDeviationWithoutPublishing()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Discovered a bigger issue.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"The fix requires a schema migration not covered by the plan."}
            """));

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.MaterialPlanDeviation, outcome.State.WaitingReason);
        Assert.Empty(provider.MergeRequests);
        Assert.Contains("schema migration", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncRunsCorrectivePassWhenChangesAreUncommitted()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"First pass.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Committed remaining changes.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var uncommittedGit = new UncommittedThenCleanGitManager(git);
        var deps = new WorkflowDependencies(provider, uncommittedGit, CreateContextBuilder(), notifier, clock);
        var workflow = new ImplementationWorkflow(deps);

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(2, omp.RunRequests.Count);
        var updated = provider.UpdatedComments[^1];
        Assert.Contains("Committed remaining changes.", updated.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncCorrectiveMaterialDeviationPausesWithoutPublishing()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"First pass.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Corrective pass needs a migration.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["Requires a migration."],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"The remaining changes require an unapproved migration."}"""));
        var workflow = new ImplementationWorkflow(new WorkflowDependencies(
            provider, new UncommittedThenCleanGitManager(git), CreateContextBuilder(), notifier, clock));

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.MaterialPlanDeviation, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }


    [Fact]
    public async Task RunAsyncFailsWithoutPublishingWhenCorrectivePassFails()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"First pass.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpErrorEvent("session-1", clock.UtcNow, "Corrective pass failed", WasCancelled: false));
        var workflow = new ImplementationWorkflow(new WorkflowDependencies(
            provider, new UncommittedThenCleanGitManager(git), CreateContextBuilder(), notifier, clock));

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncFailsWithoutPublishingWhenCorrectivePassLeavesWorktreeDirty()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"First pass.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Corrective pass.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));
        var workflow = new ImplementationWorkflow(new WorkflowDependencies(
            provider, new UncommittedThenCleanGitManager(git, dirtyReports: 2), CreateContextBuilder(), notifier, clock));

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }
    [Fact]
    public async Task RunAsyncBlocksImplementationWhenPlanIsStale()
    {
        var state = await SeedApprovedPlanAsync();
        provider.AddIssue(Repository, 1, "Bug", "Description changed after approval", null);
        provider.Issues[(Repository.Id, 1)] = provider.Issues[(Repository.Id, 1)] with { UpdatedAt = clock.UtcNow.AddHours(1) };

        var workflow = CreateWorkflow();
        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WaitingReason.ReplanRequired, outcome.State.WaitingReason);
        Assert.Empty(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncRecoversPublishedBranchWithoutRepeatingImplementationWhenResultIsDurablyRecorded()
    {
        var plannedState = await SeedApprovedPlanAsync();
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
        };
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var existing = CanonicalCommentMarkdown.Parse(canonical.Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(existing with
            {
                ImplementationResult = "**Durably recorded implementation summary.**",
                State = CanonicalStateSerializer.ToDocument(interruptedState, null),
            }),
            CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            ["agent:phase:implementing", "agent:state:waiting", "agent:cmd:continue"];
        var reviewFeedbackSnapshotCaptured = false;
        provider.OnReviewThreadsEnumeration = () => reviewFeedbackSnapshotCaptured = true;
        var omp = new FakeOmpClient();

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            interruptedState,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Empty(omp.RunRequests);
        Assert.Single(provider.MergeRequests);
        Assert.Contains("Durably recorded implementation summary.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
        Assert.True(reviewFeedbackSnapshotCaptured);
    }

    [Fact]
    public async Task RunAsyncRedoesImplementationWhenNoDurableResultWasRecordedBeforeInterruption()
    {
        var plannedState = await SeedApprovedPlanAsync();
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
        };
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var existing = CanonicalCommentMarkdown.Parse(canonical.Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(existing with
            {
                State = CanonicalStateSerializer.ToDocument(interruptedState, null),
            }),
            CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            ["agent:phase:implementing", "agent:state:waiting", "agent:cmd:continue"];
        // A remote branch happens to exist, but no durable implementation result was ever recorded
        // and no MR exists: this must never be trusted as a completed, published implementation.
        git.RemoteBranchCommitToReturn = git.BranchCommitToReturn;
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Redone from scratch.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            interruptedState,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Single(omp.RunRequests);
        Assert.Contains("Redone from scratch.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }


    [Fact]
    public async Task RunAsyncContinueAfterNewInputPauseResumesWithoutResettingRetainedWorktree()
    {
        var state = await SeedApprovedPlanAsync();
        var pausingOmp = new FakeOmpClient();
        pausingOmp.EnqueueRun(
            onStart: () => provider.AddComment(Repository, 1, "alice", "One more thing.", clock.UtcNow.AddMinutes(1)),
            new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Paused mid-flight.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var pausedOutcome = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, pausingOmp, CancellationToken.None);

        Assert.Equal(WaitingReason.NewInputDuringImplementation, pausedOutcome.State.WaitingReason);
        Assert.Equal(1, git.ResetWorktreeCallCount);

        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            ["agent:phase:implementing", "agent:state:waiting", "agent:cmd:continue"];
        var resumingOmp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Continued after acknowledgement.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var resumedOutcome = await CreateWorkflow().RunAsync(
            CreateConfig() with { ImplementationRole = "implementer" },
            WorkflowMode.Full,
            1,
            pausedOutcome.State,
            resumingOmp,
            CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, resumedOutcome.State.Phase);
        Assert.Single(resumingOmp.RunRequests);
        Assert.Equal(1, git.ResetWorktreeCallCount);
        Assert.Equal(["implementer"], resumingOmp.SelectedRoles);
        Assert.Contains("Continued after acknowledgement.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkflowCommandLabels.Continue, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
    }


    [Fact]
    public async Task RunAsyncRebuildsPromptContextAndPausesWhenInputChangesDuringContextConstruction()
    {
        var state = await SeedApprovedPlanAsync();
        var inputChanged = false;
        provider.OnIssueRelationshipsEnumeration = () =>
        {
            if (!inputChanged)
            {
                inputChanged = true;
                provider.Issues[(Repository.Id, 1)] = provider.Issues[(Repository.Id, 1)] with
                {
                    Description = "Revised requirements from the issue.",
                    UpdatedAt = clock.UtcNow.AddMinutes(1),
                };
                provider.AddComment(
                    Repository,
                    1,
                    "alice",
                    "The implementation must retain the existing API.",
                    clock.UtcNow.AddMinutes(1));
            }
        };
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Implemented the plan.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            state,
            omp,
            CancellationToken.None);

        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Contains(
            "Revised requirements from the issue.",
            Assert.Single(omp.RunRequests).Prompt,
            StringComparison.Ordinal);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncPausesBeforePushWhenHumanInputArrivesDuringImplementation()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient();
        omp.EnqueueRun(
            onStart: () => provider.AddComment(
                Repository,
                1,
                "alice",
                "Please also preserve empty titles.",
                clock.UtcNow.AddMinutes(1)),
            new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Implemented the plan.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            state,
            omp,
            CancellationToken.None);

        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Empty(provider.MergeRequests);
        Assert.Contains("New or edited human input", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Implemented the plan.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncPausesBeforePushForBotInputWhenConfigured()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient();
        omp.EnqueueRun(
            onStart: () => provider.AddComment(
                Repository,
                1,
                "review-bot",
                "Automated finding.",
                clock.UtcNow.AddMinutes(1),
                isBot: true),
            new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Implemented the plan.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow(ignoreBotComments: false).RunAsync(
            CreateConfig() with { IgnoreBotComments = false },
            WorkflowMode.Full,
            1,
            state,
            omp,
            CancellationToken.None);

        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
    }

    [Fact]
    public async Task RunAsyncRecoveryPreservesReviewSnapshotAndExposesUnobservedFeedback()
    {
        var plannedState = await SeedApprovedPlanAsync();
        var checkpointCutoff = clock.UtcNow;
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            ReviewFeedbackCutoff = checkpointCutoff,
            ReviewFeedbackIds = new HashSet<string>(StringComparer.Ordinal) { "comment:1" },
        };
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var content = CanonicalCommentMarkdown.Parse(canonical.Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(content with
            {
                ImplementationResult = "Durably recorded implementation summary.",
                State = CanonicalStateSerializer.ToDocument(interruptedState, "github/octo/widgets#1"),
            }),
            CancellationToken.None);
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(2, "bob", "Feedback at the observation boundary.", checkpointCutoff, checkpointCutoff,
                new AttachmentSource("merge-request-comment", "2"), false),
        ];

        var recovered = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            interruptedState,
            new FakeOmpClient(),
            CancellationToken.None);

        Assert.Equal(checkpointCutoff, recovered.State.ReviewFeedbackCutoff);
        Assert.Equal(["comment:1"], recovered.State.ReviewFeedbackIds);

        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowCommandLabels.Revise);
        var revisionOmp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Addressed feedback.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));
        await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, recovered.State, revisionOmp, CancellationToken.None);

        Assert.Contains("Feedback at the observation boundary.", Assert.Single(revisionOmp.RunRequests).Prompt, StringComparison.Ordinal);
    }

    private async Task<WorkflowState> SeedApprovedPlanAsync()
    {
        provider.AddIssue(Repository, 1, "Bug", "Original description");
        var workflowId = WorkflowId.New();
        var state = new WorkflowState(
            workflowId, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval,
            1, 1, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow.AddHours(-1),
            PlanInputHash: PlanInputHasher.Compute("Bug", "Original description"));
        var document = CanonicalStateSerializer.ToDocument(state, null);
        var content = new CanonicalCommentContent("Approved plan text.", ["Decision one."], null, document);
        await provider.CreateIssueCommentAsync(Repository, 1, CanonicalCommentMarkdown.Render(content), CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] = ["agent:phase:planned", "agent:state:waiting", "agent:cmd:implement"];
        Directory.CreateDirectory(Path.Combine(workspaceRoot, workflowId.ToString(), "worktree"));
        return state;
    }

    private ImplementationWorkflow CreateWorkflow(bool ignoreBotComments = true) =>
        new(new WorkflowDependencies(provider, git, CreateContextBuilder(ignoreBotComments), notifier, clock));

    private AgentContextBuilder CreateContextBuilder(bool ignoreBotComments = true)
    {
        var attachmentPipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        return new AgentContextBuilder(
            provider,
            attachmentPipeline,
            new AgentContextBuilderOptions { IgnoreBotComments = ignoreBotComments });
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

    /// <summary>Wraps a real <see cref="FakeGitRepositoryManager"/> and reports uncommitted changes
    /// for a controlled number of probes, so publication gates can be exercised deterministically.</summary>
    private sealed class UncommittedThenCleanGitManager(FakeGitRepositoryManager inner, int dirtyReports = 1) : IGitRepositoryManager
    {
        private int reported;

        public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) => inner.EnsureBareRepositoryAsync(repositoryId, cloneUrl, authentication, cancellationToken);
        public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) => inner.FetchAsync(repositoryId, authentication, cancellationToken);
        public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.ResolveBranchCommitAsync(repositoryId, branchName, cancellationToken);
        public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.TryResolveRemoteBranchCommitAsync(repositoryId, branchName, cancellationToken);
        public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) => inner.IsAncestorAsync(repositoryId, ancestorCommit, descendantCommit, cancellationToken);
        public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) => inner.CreateWorktreeAsync(repositoryId, worktreeId, worktreePath, branchName, baseCommit, cancellationToken);
        public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken) => inner.ResetWorktreeAsync(repositoryId, worktreePath, commit, cancellationToken);

        public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken)
        {
            if (reported++ < dirtyReports)
            {
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }

        public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => inner.GetHeadCommitAsync(repositoryId, worktreePath, cancellationToken);
        public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) => inner.UpdateSubmodulesAsync(repositoryId, worktreePath, authenticationResolver, cancellationToken);
        public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => inner.TryRebaseOntoAsync(repositoryId, worktreePath, ontoCommit, identity, cancellationToken);
        public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => inner.TryMergeAsync(repositoryId, worktreePath, commit, identity, cancellationToken);
        public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => inner.PushAsync(repositoryId, worktreePath, branchName, authentication, cancellationToken);
        public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) => inner.RemoveWorktreeAsync(repositoryId, worktreeId, worktreePath, cancellationToken);
        public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.RemoveLocalBranchAsync(repositoryId, branchName, cancellationToken);
        public bool WorktreeRequiresLfs(string worktreePath) => inner.WorktreeRequiresLfs(worktreePath);
        public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken) => inner.MaterializeLfsContentAsync(repositoryId, worktreePath, authentication, cancellationToken);
        public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => inner.UploadLfsObjectsAsync(repositoryId, worktreePath, branchName, authentication, cancellationToken);
    }
}
