using System.Security.Cryptography;
using System.Text;

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
        var persisted = CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State;
        Assert.Equal("planned", persisted.Phase);
        Assert.Equal("waiting", persisted.State);
        Assert.Equal("plan-approval", persisted.WaitingReason);
    }


    [Fact]
    public async Task RunAsyncPublishesDraftMergeRequestAndSetsReviewWaiting()
    {
        var state = await SeedApprovedPlanAsync();
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
        Assert.Contains(
            "## Linked pull/merge request [#1](https://fake-provider.example/octo/widgets/merge_requests/1)",
            updated.Body,
            StringComparison.Ordinal);
        var publishedState = CanonicalCommentMarkdown.Parse(updated.Body).State;
        Assert.Equal(git.BranchCommitToReturn, publishedState.ExpectedImplementationHead);
        Assert.Equal("branch-published", publishedState.PublicationStage);
        Assert.Matches("^[0-9a-f]{64}$", publishedState.ImplementationInputDigest);
        Assert.Equal("abc123", publishedState.RebasedPublicationBase);
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:review");
        Assert.Equal(1, git.PublishChangedSubmodulesCallCount);
    }

    [Fact]
    public async Task RunAsyncAfterReviewReplanMergesTargetWithoutRewritingPublishedBranch()
    {
        var state = (await SeedApprovedPlanAsync()) with
        {
            BaseCommit = "def456",
            ExpectedImplementationHead = "def456",
            PublicationStage = ImplementationPublicationStage.BranchPublished,
            ReviewFeedbackCutoff = clock.UtcNow.AddHours(-1),
            ReviewFeedbackVersions = new HashSet<string>(StringComparer.Ordinal) { "comment:prior" },
        };
        AddPublishedMergeRequest(state);
        var canonical = CanonicalCommentMarkdown.Parse(
            Assert.Single(provider.IssueComments[(Repository.Id, 1)]).Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            Assert.Single(provider.IssueComments[(Repository.Id, 1)]).Id,
            CanonicalCommentMarkdown.Render(canonical with
            {
                State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#1"),
            }),
            CancellationToken.None);
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "reviewer", "Keep the public API stable.", clock.UtcNow, clock.UtcNow,
                new AttachmentSource("merge-request-comment", "1"), false),
        ];
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Applied the revised plan.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}
            """));

        var outcome = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(1, git.MergeAttempts);
        Assert.Equal(0, git.RebaseAttempts);
        var request = Assert.Single(omp.RunRequests);
        Assert.Contains("Keep the public API stable.", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(state.ReviewFeedbackCutoff, outcome.State.ReviewFeedbackCutoff);
        Assert.Equal(state.ReviewFeedbackVersions, outcome.State.ReviewFeedbackVersions);
    }

    [Fact]
    public async Task RunAsyncPausesWhenReimplementationRequestWasRetargeted()
    {
        var state = (await SeedApprovedPlanAsync()) with
        {
            ExpectedImplementationHead = "def456",
            PublicationStage = ImplementationPublicationStage.BranchPublished,
        };
        AddPublishedMergeRequest(state);
        var canonical = CanonicalCommentMarkdown.Parse(Assert.Single(provider.IssueComments[(Repository.Id, 1)]).Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            Assert.Single(provider.IssueComments[(Repository.Id, 1)]).Id,
            CanonicalCommentMarkdown.Render(canonical with
            {
                State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#1"),
            }),
            CancellationToken.None);
        provider.MergeRequests[1] = provider.MergeRequests[1] with { TargetBranch = "other-target" };

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(), WorkflowMode.Full, 1, state, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WaitingReason.ManualIntervention, outcome.State.WaitingReason);
        Assert.Empty(git.ResetWorktrees);
    }


    [Fact]
    public async Task RunAsyncAdoptsMarkerMatchedMergeRequestBeforeCheckpointingItsIdentity()
    {
        var state = await SeedApprovedPlanAsync();
        provider.CreatedMergeRequestResponse = mergeRequest => mergeRequest with
        {
            Number = 999,
            Description = "untrusted creation response",
        };
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Implemented.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}
            """));

        await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        var finalState = CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State;
        Assert.Equal($"{Repository.Id}#1", finalState.PullOrMergeRequest);
    }

    [Fact]
    public async Task RunAsyncRetriesContextUntilInputBaselineIsStable()
    {
        var state = await SeedApprovedPlanAsync();
        var changes = 0;
        provider.OnIssueRelationshipsEnumeration = () =>
        {
            if (changes++ < 2)
            {
                provider.AddComment(
                    Repository,
                    1,
                    "alice",
                    $"Constraint {changes}",
                    clock.UtcNow.AddMinutes(changes));
            }
        };
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1", clock.UtcNow,
            """{"summary":"Implemented.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task RunAsyncPausesWhenInputArrivesDuringLfsUpload()
    {
        var state = await SeedApprovedPlanAsync();
        git.LfsRequired = true;
        git.OnLfsUpload = () => provider.AddComment(Repository, 1, "alice", "Please include this constraint.", clock.UtcNow.AddMinutes(1));
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1", clock.UtcNow,
            """{"summary":"Implemented.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncTreatsHumanCopiedLocatorAsNewInputDuringPublication()
    {
        var state = await SeedApprovedPlanAsync();
        git.LfsRequired = true;
        git.OnLfsUpload = () => provider.AddComment(
            Repository,
            1,
            "alice",
            CanonicalCommentMarkdown.StateLocatorMarker,
            clock.UtcNow.AddMinutes(1));
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Implemented.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncLeavesInitialReviewFeedbackUncheckpointedForFirstRevision()
    {
        var state = await SeedApprovedPlanAsync();
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "bob", "Please add validation.", clock.UtcNow, clock.UtcNow,
                new AttachmentSource("merge-request-comment", "1"), false),
        ];
        var implementationOmp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1", clock.UtcNow,
            """{"summary":"Implemented.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var review = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, implementationOmp, CancellationToken.None);
        var revisionOmp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1", clock.UtcNow,
            """{"summary":"Addressed feedback.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, review.State, revisionOmp, CancellationToken.None);

        Assert.Empty(review.State.ReviewFeedbackVersions!);
        Assert.Contains("Please add validation.", Assert.Single(revisionOmp.RunRequests).Prompt, StringComparison.Ordinal);
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
    public async Task RunAsyncPausesForMaterialDeviationFromFirstPublicationConflictResolutionBeforeLfsOrPush()
    {
        var state = await SeedApprovedPlanAsync();
        git.BranchCommitToReturn = "latest-target";
        git.LfsRequired = true;
        var lfsUploadAttempted = false;
        git.OnLfsUpload = () => lfsUploadAttempted = true;
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Implemented.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Conflict requires a migration.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["The target conflict exposes an unapproved schema migration."],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"Resolving the target conflict requires an unapproved schema migration."}"""));
        var workflow = new ImplementationWorkflow(new WorkflowDependencies(
            provider,
            new UncommittedThenCleanGitManager(git, dirtyReports: 0, rebaseSucceeds: false),
            CreateContextBuilder(),
            notifier,
            clock));

        var outcome = await workflow.RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.MaterialPlanDeviation, outcome.State.WaitingReason);
        Assert.False(lfsUploadAttempted);
        Assert.Equal(0, git.PushCallCount);
        Assert.Contains("Conflict requires a migration.", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult, StringComparison.Ordinal);
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
            ExpectedImplementationHead = git.RemoteBranchCommitToReturn,
            PublicationStage = ImplementationPublicationStage.BranchPublished,
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
    }

    [Fact]
    public async Task RunAsyncDoesNotRecoverOrCreateMergeRequestFromAnOlderDeterministicBranch()
    {
        var plannedState = await SeedApprovedPlanAsync();
        git.BranchCommitToReturn = "beadfeed";
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            ExpectedImplementationHead = "beadfeed",
            PublicationStage = ImplementationPublicationStage.ResultCheckpointed,
            ImplementationInputDigest = ImplementationInputDigest(),
            RebasedPublicationBase = "feedface",
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
        git.RemoteBranchCommitToReturn = "deadbeef";

        var omp = new FakeOmpClient();
        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            interruptedState,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.ManualIntervention, outcome.State.WaitingReason);
        Assert.Empty(omp.RunRequests);
        Assert.Empty(provider.MergeRequests);
        Assert.Contains("does not match the checkpointed implementation head", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncRecoversReviewPublicationWhenRemoteMatchesResultCheckpoint()
    {
        var plannedState = await SeedApprovedPlanAsync();
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            ExpectedImplementationHead = git.RemoteBranchCommitToReturn,
            PublicationStage = ImplementationPublicationStage.ResultCheckpointed,
            ImplementationInputDigest = ImplementationInputDigest(),
            RebasedPublicationBase = "feedface",
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

        var omp = new FakeOmpClient();
        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            interruptedState,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(ImplementationPublicationStage.BranchPublished, outcome.State.PublicationStage);
        Assert.Empty(omp.RunRequests);
        Assert.Equal(0, git.PushCallCount);
        Assert.Single(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncReplaysPublicationTailUsingCheckpointedRebasedBase()
    {
        var plannedState = await SeedApprovedPlanAsync();
        git.RemoteBranchCommitToReturn = null;
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            ExpectedImplementationHead = git.BranchCommitToReturn,
            PublicationStage = ImplementationPublicationStage.ResultCheckpointed,
            ImplementationInputDigest = ImplementationInputDigest(),
            RebasedPublicationBase = "feedface",
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
        Assert.Equal(1, git.PushCallCount);
        Assert.Single(provider.MergeRequests);
        Assert.Equal(ImplementationPublicationStage.BranchPublished, outcome.State.PublicationStage);
        Assert.Equal(["feedface"], git.PublishedSubmoduleBaseCommits);
    }

    [Fact]
    public async Task RunAsyncRecoveryPausesBeforePushWhenInputArrivesDuringLfsUpload()
    {
        var plannedState = await SeedApprovedPlanAsync();
        git.RemoteBranchCommitToReturn = null;
        git.LfsRequired = true;
        git.OnLfsUpload = () => provider.AddComment(
            Repository,
            1,
            "alice",
            "Please include this recovery constraint.",
            clock.UtcNow.AddMinutes(1));
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            ExpectedImplementationHead = git.BranchCommitToReturn,
            PublicationStage = ImplementationPublicationStage.ResultCheckpointed,
            ImplementationInputDigest = ImplementationInputDigest(),
            RebasedPublicationBase = "feedface",
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

        var omp = new FakeOmpClient();
        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            interruptedState,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Equal(ImplementationPublicationStage.ResultCheckpointed, outcome.State.PublicationStage);
        Assert.Empty(omp.RunRequests);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncRecoveryPausesBeforePublicationReplayWhenCheckpointInputChanged()
    {
        var plannedState = await SeedApprovedPlanAsync();
        git.RemoteBranchCommitToReturn = null;
        var interruptedState = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            ExpectedImplementationHead = git.BranchCommitToReturn,
            PublicationStage = ImplementationPublicationStage.ResultCheckpointed,
            ImplementationInputDigest = ImplementationInputDigest(),
            RebasedPublicationBase = "feedface",
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
        provider.AddComment(
            Repository,
            1,
            "alice",
            "Please include this checkpointed recovery constraint.",
            clock.UtcNow.AddMinutes(1));

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            interruptedState,
            new FakeOmpClient(),
            CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Equal(0, git.PublishChangedSubmodulesCallCount);
        Assert.Equal(0, git.PushCallCount);
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
    public async Task RunAsyncContinuationPausesBeforePushWhenLinkedRequestDescriptionChangesDuringLfsUpload()
    {
        var plannedState = await SeedApprovedPlanAsync();
        var state = plannedState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.NewInputDuringImplementation,
            PublicationStage = ImplementationPublicationStage.BranchPublished,
        };
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var content = CanonicalCommentMarkdown.Parse(canonical.Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(content with
            {
                ImplementationResult = "Retained implementation result.",
                State = CanonicalStateSerializer.ToDocument(state, "github/octo/widgets#1"),
            }),
            CancellationToken.None);
        AddPublishedMergeRequest(state);
        git.LfsRequired = true;
        git.OnLfsUpload = () => provider.MergeRequests[1] = provider.MergeRequests[1] with
        {
            Description = "Edited linked request description.",
        };
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Continued implementation.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.NewInputDuringImplementation, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
    }



    [Fact]
    public async Task RunAsyncPausesForReplanBeforePromptWhenTitleOrDescriptionChangesDuringContextConstruction()
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
            }
        };

        var omp = new FakeOmpClient();
        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            state,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WaitingReason.ReplanRequired, outcome.State.WaitingReason);
        Assert.Empty(omp.RunRequests);
        Assert.Equal(0, git.PushCallCount);
        Assert.Empty(provider.MergeRequests);
    }

    [Fact]
    public async Task RunAsyncPausesForReplanBeforePushWhenTitleOrDescriptionChangesDuringImplementation()
    {
        var state = await SeedApprovedPlanAsync();
        var omp = new FakeOmpClient();
        omp.EnqueueRun(
            onStart: () => provider.Issues[(Repository.Id, 1)] = provider.Issues[(Repository.Id, 1)] with
            {
                Title = "Bug with revised requirements",
                UpdatedAt = clock.UtcNow.AddMinutes(1),
            },
            new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Implemented the approved plan.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(
            CreateConfig(),
            WorkflowMode.Full,
            1,
            state,
            omp,
            CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Planned, outcome.State.Phase);
        Assert.Equal(WaitingReason.ReplanRequired, outcome.State.WaitingReason);
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
            ReviewFeedbackVersions = new HashSet<string>(StringComparer.Ordinal) { "comment:1" },
            ExpectedImplementationHead = git.RemoteBranchCommitToReturn,
            PublicationStage = ImplementationPublicationStage.BranchPublished,
        };
        AddPublishedMergeRequest(interruptedState);
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
        Assert.Equal(["comment:1"], recovered.State.ReviewFeedbackVersions);

        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowCommandLabels.Revise);
        var revisionOmp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Addressed feedback.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));
        await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, recovered.State, revisionOmp, CancellationToken.None);

        Assert.Contains("Feedback at the observation boundary.", Assert.Single(revisionOmp.RunRequests).Prompt, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("Authentication failed for remote.", WaitingReason.MissingCredentials)]
    [InlineData("Push rejected: protected branch hook declined.", WaitingReason.ProtectedBranch)]
    public async Task RunAsyncPreservesResultCheckpointWhenInitialPublicationIsBlocked(
        string publicationFailure,
        WaitingReason expectedWaitingReason)
    {
        var state = await SeedApprovedPlanAsync();
        git.PushException = new InvalidOperationException(publicationFailure);
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Implemented.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await CreateWorkflow().RunAsync(CreateConfig(), WorkflowMode.Full, 1, state, omp, CancellationToken.None);

        var persisted = CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body);
        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Implementing, outcome.State.Phase);
        Assert.Equal(expectedWaitingReason, outcome.State.WaitingReason);
        Assert.Equal(ImplementationPublicationStage.ResultCheckpointed, outcome.State.PublicationStage);
        Assert.Equal(git.BranchCommitToReturn, outcome.State.ExpectedImplementationHead);
        Assert.Equal(
            ImplementationPublicationStage.ResultCheckpointed,
            CanonicalStateSerializer.ToWorkflowState(persisted.State).PublicationStage);
        Assert.Equal(git.BranchCommitToReturn, persisted.State.ExpectedImplementationHead);
        Assert.Equal("Implemented.", persisted.ImplementationResult);
        Assert.Equal(WorkflowNotificationKind.HumanActionRequired, Assert.Single(notifier.Notifications).Kind);
    }


    private static string ImplementationInputDigest()
    {
        var commentsDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty)));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"Bug\0Original description\0{commentsDigest}")));
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

    private void AddPublishedMergeRequest(WorkflowState state)
    {
        provider.MergeRequests[1] = new ProviderMergeRequest(
            Repository, 1, state.Branch, state.TargetBranch, "Fix",
            $"<!-- issue-agent:workflow:{state.WorkflowId} -->",
            IsDraft: true, IsMerged: false, IsClosed: false,
            new AttachmentSource("merge-request-description", "1"));
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
    private sealed class UncommittedThenCleanGitManager(
        FakeGitRepositoryManager inner,
        int dirtyReports = 1,
        bool rebaseSucceeds = true) : IGitRepositoryManager
    {
        private int reported;

        public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) => inner.EnsureBareRepositoryAsync(repositoryId, cloneUrl, authentication, cancellationToken);
        public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) => inner.FetchAsync(repositoryId, authentication, cancellationToken);
        public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.ResolveBranchCommitAsync(repositoryId, branchName, cancellationToken);
        public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.TryResolveRemoteBranchCommitAsync(repositoryId, branchName, cancellationToken);
        public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) => inner.IsAncestorAsync(repositoryId, ancestorCommit, descendantCommit, cancellationToken);
        public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) => inner.CreateWorktreeAsync(repositoryId, worktreeId, worktreePath, branchName, baseCommit, cancellationToken);
        public ValueTask RenameWorktreeBranchAsync(string repositoryId, string worktreePath, string expectedCurrentBranch, string newBranchName, CancellationToken cancellationToken) => inner.RenameWorktreeBranchAsync(repositoryId, worktreePath, expectedCurrentBranch, newBranchName, cancellationToken);
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
        public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(rebaseSucceeds);
        public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => inner.TryMergeAsync(repositoryId, worktreePath, commit, identity, cancellationToken);
        public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => inner.PushAsync(repositoryId, worktreePath, branchName, authentication, cancellationToken);
        public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) => inner.RemoveWorktreeAsync(repositoryId, worktreeId, worktreePath, cancellationToken);
        public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => inner.RemoveLocalBranchAsync(repositoryId, branchName, cancellationToken);
        public bool WorktreeRequiresLfs(string worktreePath) => inner.WorktreeRequiresLfs(worktreePath);
        public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, Func<string, GitAuthentication?> submoduleAuthenticationResolver, CancellationToken cancellationToken) => inner.MaterializeLfsContentAsync(repositoryId, worktreePath, authentication, submoduleAuthenticationResolver, cancellationToken);
        public ValueTask PublishChangedSubmodulesAsync(string repositoryId, string worktreePath, string baseCommit, string branchName, GitAuthentication authentication, Func<string, GitAuthentication?> submoduleAuthenticationResolver, CancellationToken cancellationToken) => inner.PublishChangedSubmodulesAsync(repositoryId, worktreePath, baseCommit, branchName, authentication, submoduleAuthenticationResolver, cancellationToken);

        public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => inner.UploadLfsObjectsAsync(repositoryId, worktreePath, branchName, authentication, cancellationToken);
    }
}
