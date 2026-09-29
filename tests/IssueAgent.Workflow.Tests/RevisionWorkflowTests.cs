using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class RevisionWorkflowTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-revision-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task RunAsyncCollectsReviewFeedbackAndRepublishesInPlace()
    {
        var state = await SeedReviewStateAsync();
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "bob", "Please rename this variable.", clock.UtcNow, clock.UtcNow, new AttachmentSource("merge-request-comment", "1"), false),
        ];
        provider.ReviewThreads[(Repository.Id, 1)] =
        [
            new ProviderReviewThread(
                "thread-1",
                IsResolved: true,
                [
                    new ProviderComment(2, "carol", "This concern is resolved.", clock.UtcNow, clock.UtcNow,
                        new AttachmentSource("review-thread-comment", "2"), false),
                ]),
        ];

        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """
            {"summary":"Renamed the variable.","keyChanges":["Renamed x to itemCount"],"decisions":[],"checksRun":["dotnet test"],"knownFailures":[],"deviations":[],"risks":[]}
            """));

        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var outcome = await workflow.RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(git.BranchCommitToReturn, outcome.State.ExpectedImplementationHead);
        Assert.Equal(WaitingReason.ReviewRequested, outcome.State.WaitingReason);

        var request = Assert.Single(omp.RunRequests);
        Assert.Contains("Please rename this variable.", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("resolved: true", request.Prompt, StringComparison.Ordinal);

        var checkpoints = provider.UpdatedComments;
        Assert.Equal(3, checkpoints.Count);
        Assert.Contains("phase: revising", checkpoints[0].Body, StringComparison.Ordinal);
        Assert.Contains("state: working", checkpoints[0].Body, StringComparison.Ordinal);
        Assert.Contains("Renamed the variable.", checkpoints[1].Body, StringComparison.Ordinal);
        Assert.Contains("Renamed the variable.", checkpoints[2].Body, StringComparison.Ordinal);
        var checkpoint = CanonicalCommentMarkdown.Parse(checkpoints[1].Body);
        Assert.Equal("revising", checkpoint.State.Phase);
        Assert.Equal("working", checkpoint.State.State);
        Assert.Contains(provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)], l => l == "agent:phase:review");
        var publishedCheckpoint = CanonicalCommentMarkdown.Parse(checkpoints[2].Body).State;
        Assert.Equal(clock.UtcNow, publishedCheckpoint.ReviewFeedbackCutoff);
        Assert.All(
            ["comment:1:2024-06-01T00:00:00.0000000+00:00:757E7119D1C00D8C550AF13798B5AF8549B6674AB561F9F31599A7ECC2B6B5A2",
             "thread:thread-1:2:2024-06-01T00:00:00.0000000+00:00:59C5AE315EFFE42CAC3801A766ED7BEEB77D9A7A1E764312DDCC0AB3AD426F22",
             "thread:thread-1:resolved=True"],
            expected => Assert.Contains(expected, publishedCheckpoint.ReviewFeedbackVersions!));
        Assert.Contains(publishedCheckpoint.ReviewFeedbackVersions!, version =>
            version.StartsWith("merge-request:1:metadata:", StringComparison.Ordinal));
        Assert.DoesNotContain("agent:cmd:revise", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
    }
    [Fact]
    public async Task RunAsyncPausesWhenMergeRequestMetadataChangesDuringRevision()
    {
        var state = await SeedReviewStateAsync();
        var changed = false;
        provider.OnReviewThreadsEnumeration = () =>
        {
            if (changed)
            {
                return;
            }

            changed = true;
            provider.MergeRequests[1] = provider.MergeRequests[1] with
            {
                Description = "Updated while OMP was revising.",
            };
        };
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.NewFeedbackDuringRevision, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
        Assert.Single(omp.RunRequests);
    }


    [Fact]
    public async Task RunAsyncPausesWhenStoredRequestWasRetargeted()
    {
        var state = await SeedReviewStateAsync();
        provider.MergeRequests[1] = provider.MergeRequests[1] with
        {
            SourceBranch = "renamed-agent-branch",
            TargetBranch = "renamed-target",
        };

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Revising, outcome.State.Phase);
        Assert.Equal(WaitingReason.ManualIntervention, outcome.State.WaitingReason);
        Assert.Equal(
            "Initial implementation summary.",
            CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult);
        Assert.DoesNotContain(notifier.Notifications, notification => notification.Kind == WorkflowNotificationKind.RevisionFailed);
    }


    [Fact]
    public async Task RevisionPromptIncludesAvailableAndOmittedReviewAttachments()
    {
        var state = await SeedReviewStateAsync();
        var context = new AgentContext(
            new IssueContext(Repository.Id, 1, "Bug", "Description", [], [], []),
            [],
            null,
            new MergeRequestContext(
                1,
                "Description",
                [],
                [],
                [
                    new AttachmentReference(
                        "https://example.test/review.log",
                        "review.log",
                        "/workspace/workflows/1/attachments/review.log",
                        "review-thread-comment:12:thread-3",
                        42),
                    new AttachmentReference(
                        "https://example.test/large.zip",
                        "large.zip",
                        string.Empty,
                        "merge-request-comment:13",
                        0,
                        IsOmitted: true,
                        OmissionReason: "attachment exceeds the configured size limit"),
                ]),
            state);

        var prompt = ImplementationPromptBuilder.BuildRevisionPrompt(context, []);

        Assert.Contains("## Review attachments", prompt, StringComparison.Ordinal);
        Assert.Contains("AVAILABLE review.log (review-thread-comment:12:thread-3): /workspace/workflows/1/attachments/review.log", prompt, StringComparison.Ordinal);
        Assert.Contains("OMITTED large.zip (merge-request-comment:13): attachment exceeds the configured size limit", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncIncludesFeedbackEditedAfterThePriorCutoff()
    {
        var state = await SeedReviewStateAsync();
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "bob", "Edited feedback.", clock.UtcNow.AddHours(-2), clock.UtcNow,
                new AttachmentSource("merge-request-comment", "1"), false),
        ];
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1", clock.UtcNow,
            """{"summary":"Addressed the edit.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Contains("Edited feedback.", Assert.Single(omp.RunRequests).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncIncludesFeedbackEditedAtThePublishedCutoff()
    {
        var state = await SeedReviewStateAsync();
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "bob", "Original feedback.", clock.UtcNow, clock.UtcNow,
                new AttachmentSource("merge-request-comment", "1"), false),
        ];
        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var published = await workflow.RunAsync(
            CreateConfig(),
            1,
            state,
            new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Addressed the original feedback.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}""")),
            CancellationToken.None);

        Assert.Contains(published.State.ReviewFeedbackVersions!, version => version.StartsWith("comment:1:", StringComparison.Ordinal));
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "bob", "Edited feedback at the same timestamp.", clock.UtcNow, clock.UtcNow,
                new AttachmentSource("merge-request-comment", "1"), false),
        ];
        var revisionOmp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Addressed the edited feedback.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        await workflow.RunAsync(CreateConfig(), 1, published.State, revisionOmp, CancellationToken.None);

        Assert.Contains("Edited feedback at the same timestamp.", Assert.Single(revisionOmp.RunRequests).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncIncludesBotFeedbackWhenConfigured()
    {
        var state = await SeedReviewStateAsync();
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(1, "review-bot", "Automated review finding.", clock.UtcNow, clock.UtcNow,
                new AttachmentSource("merge-request-comment", "1"), true),
        ];
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1", clock.UtcNow,
            """{"summary":"Addressed automated feedback.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(ignoreBotComments: false), notifier, clock))
            .RunAsync(CreateConfig() with { IgnoreBotComments = false }, 1, state, omp, CancellationToken.None);

        Assert.Contains("Automated review finding.", Assert.Single(omp.RunRequests).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncUsesFeedbackObservationStartAsPublishedCutoff()
    {
        var state = await SeedReviewStateAsync();
        provider.OnReviewThreadsEnumeration = () => clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1", clock.UtcNow,
            """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(
            DateTimeOffset.Parse("2024-06-01T00:03:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State.ReviewFeedbackCutoff);
    }

    [Fact]
    public async Task RunAsyncPersistsFailureWhenOmpResultViolatesContract()
    {
        var state = await SeedReviewStateAsync();
        var omp = new FakeOmpClient().EnqueueRun(
            new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Missing required fields."}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Null(CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult);
        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("failed", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State.Phase);
        Assert.Equal(WorkflowNotificationKind.RevisionFailed, Assert.Single(notifier.Notifications).Kind);
    }


    [Fact]
    public async Task RunAsyncUsesSameOmpSessionToResolveTargetConflictBeforePush()
    {
        var state = await SeedReviewStateAsync();
        git.MergeSucceeds = false;
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent("session-1", clock.UtcNow, """{"summary":"Conflict resolved.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));
        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(
                CreateConfig() with { RevisionRole = "reviewer", ConflictResolutionRole = "resolver" },
                1,
                state,
                omp,
                CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Equal(1, git.MergeAttempts);
        Assert.Equal(2, omp.RunRequests.Count);
        Assert.All(omp.RunRequests, request => Assert.Equal("session-1", request.SessionId));
        Assert.Equal(["reviewer", "resolver"], omp.SelectedRoles);
        Assert.Contains("conflict", omp.RunRequests[1].Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsyncRecoversConflictResolutionCheckpointAfterLfsInterruptionWithoutRerunningOmp()
    {
        var state = await SeedReviewStateAsync();
        git.MergeSucceeds = false;
        git.LfsRequired = true;
        git.OnLfsUpload = () => throw new InvalidOperationException("Simulated process stop during LFS upload.");
        var interruptedOmp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Conflict resolved.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));
        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.RunAsync(CreateConfig(), 1, state, interruptedOmp, CancellationToken.None));

        var checkpoint = CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body);
        Assert.Contains("Conflict resolved.", checkpoint.ImplementationResult, StringComparison.Ordinal);
        var recoveredState = CanonicalStateSerializer.ToWorkflowState(checkpoint.State);

        git.MergeSucceeds = true;
        git.LfsRequired = false;
        var resumedOmp = new FakeOmpClient();
        var outcome = await workflow.RunAsync(
            CreateConfig(), 1, recoveredState, resumedOmp, CancellationToken.None, publishRetainedResult: true);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Empty(resumedOmp.RunRequests);
        Assert.Equal(1, git.PushCallCount);
        Assert.Contains("Conflict resolved.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncRemoteRewriteRecoveryRunsFreshRevisionInsteadOfPublishingRetainedResult()
    {
        var reviewState = await SeedReviewStateAsync();
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var recoveredState = reviewState with
        {
            Phase = WorkflowPhase.Revising,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.RemoteHistoryRewrite,
        };
        var recoveredContent = CanonicalCommentMarkdown.Parse(canonical.Body) with
        {
            ImplementationResult = "Stale retained revision.",
            State = CanonicalStateSerializer.ToDocument(recoveredState, "github/octo/widgets#1"),
        };
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(recoveredContent),
            CancellationToken.None);
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Fresh revision from accepted remote head.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(
            provider, git, CreateContextBuilder(), notifier, clock)).RunAsync(
            CreateConfig(), 1, recoveredState, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Single(omp.RunRequests);
        Assert.Contains("Fresh revision from accepted remote head.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale retained revision.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncRecoveryPausesWhenDurableHandledIdsExcludeExistingFeedback()
    {
        var reviewState = await SeedReviewStateAsync();
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var interruptedState = reviewState with
        {
            Phase = WorkflowPhase.Revising,
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
            ReviewFeedbackCutoff = clock.UtcNow,
            ReviewFeedbackVersions = new HashSet<string>(StringComparer.Ordinal),
        };
        var interruptedContent = CanonicalCommentMarkdown.Parse(canonical.Body) with
        {
            ImplementationResult = "Retained revision result.",
            State = CanonicalStateSerializer.ToDocument(interruptedState, "github/octo/widgets#1"),
        };
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(interruptedContent),
            CancellationToken.None);
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(
                9,
                "bob",
                "Feedback that predates the cutoff but was never handled.",
                clock.UtcNow.AddHours(-1),
                clock.UtcNow.AddHours(-1),
                new AttachmentSource("merge-request-comment", "9"),
                false),
        ];

        var omp = new FakeOmpClient();
        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, interruptedState, omp, CancellationToken.None, publishRetainedResult: true);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.NewFeedbackDuringRevision, outcome.State.WaitingReason);
        Assert.Empty(omp.RunRequests);
        Assert.Equal(0, git.PushCallCount);
        var paused = CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body);
        Assert.Equal(clock.UtcNow, paused.State.ReviewFeedbackCutoff);
        Assert.Empty(paused.State.ReviewFeedbackVersions!);
        Assert.Equal("Retained revision result.", paused.ImplementationResult);
    }

    [Fact]
    public async Task RunAsyncRecoveryPausesForHumanCopiedCanonicalLocator()
    {
        var reviewState = await SeedReviewStateAsync();
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var interruptedState = reviewState with
        {
            Phase = WorkflowPhase.Revising,
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
            ReviewFeedbackCutoff = clock.UtcNow,
            ReviewFeedbackVersions = new HashSet<string>(StringComparer.Ordinal),
        };
        var interruptedContent = CanonicalCommentMarkdown.Parse(canonical.Body) with
        {
            ImplementationResult = "Retained revision result.",
            State = CanonicalStateSerializer.ToDocument(interruptedState, "github/octo/widgets#1"),
        };
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(interruptedContent),
            CancellationToken.None);
        var copiedLocator = $"Please address this copied marker:\n{CanonicalCommentMarkdown.StateLocatorMarker}";
        provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(
                9,
                "bob",
                copiedLocator,
                clock.UtcNow.AddHours(-1),
                clock.UtcNow.AddHours(-1),
                new AttachmentSource("merge-request-comment", "9"),
                false),
        ];

        var omp = new FakeOmpClient();
        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, interruptedState, omp, CancellationToken.None, publishRetainedResult: true);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.NewFeedbackDuringRevision, outcome.State.WaitingReason);
        Assert.Empty(omp.RunRequests);
        Assert.Equal(0, git.PushCallCount);
    }

    [Fact]
    public async Task RunAsyncPausesForMaterialDeviationFromConflictResolutionBeforeLfsOrPush()
    {
        var state = await SeedReviewStateAsync();
        git.MergeSucceeds = false;
        git.LfsRequired = true;
        var lfsUploadAttempted = false;
        git.OnLfsUpload = () => lfsUploadAttempted = true;
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Conflict requires a new service boundary.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["The merge conflict requires an unapproved service boundary."],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"Resolving the target conflict requires an unapproved service boundary."}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.MaterialPlanDeviation, outcome.State.WaitingReason);
        Assert.False(lfsUploadAttempted);
        Assert.Equal(0, git.PushCallCount);
        Assert.Contains("Conflict requires a new service boundary.", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncFailsWithoutPublishingWhenConflictResolutionDoesNotContainTarget()
    {
        var state = await SeedReviewStateAsync();
        git.MergeSucceeds = false;
        git.RemoteBranchIsDescendant = false;
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Conflict resolved.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, git.PushCallCount);
        Assert.Contains("does not contain the latest target commit", Assert.Single(notifier.Notifications).Message, StringComparison.Ordinal);
    }


    [Fact]
    public async Task RunAsyncFailsWithoutPublishingWhenConflictResolutionLeavesWorktreeDirty()
    {
        var state = await SeedReviewStateAsync();
        git.MergeSucceeds = false;
        git.WorktreeHasUncommittedChanges = true;
        var omp = new FakeOmpClient()
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""))
            .EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Conflict resolved.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, git.PushCallCount);
        Assert.Contains("not clean after target integration", Assert.Single(notifier.Notifications).Message, StringComparison.Ordinal);
    }
    [Fact]
    public async Task RunAsyncPausesForMaterialDeviationBeforeMergeOrPush()
    {
        var state = await SeedReviewStateAsync();
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Needs architecture change.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["Requires a new service boundary."],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"The approved plan cannot safely support the required service boundary."}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.MaterialPlanDeviation, outcome.State.WaitingReason);
        Assert.Equal(0, git.MergeAttempts);
        Assert.Contains("service boundary", Assert.Single(notifier.Notifications).Message, StringComparison.Ordinal);
        Assert.Contains("Needs architecture change.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncPausesForHumanCopiedCanonicalLocatorArrivingDuringRevision()
    {
        var state = await SeedReviewStateAsync();
        var omp = new FakeOmpClient();
        var copiedLocator = $"Please account for this copied marker:\n{CanonicalCommentMarkdown.StateLocatorMarker}";
        omp.EnqueueRun(
            onStart: () => provider.MergeRequestComments[(Repository.Id, 1)] =
            [
                new ProviderComment(9, "bob", copiedLocator, clock.UtcNow.AddMinutes(1), clock.UtcNow.AddMinutes(1),
                    new AttachmentSource("merge-request-comment", "9"), false),
            ],
            new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Revising, outcome.State.Phase);
        Assert.Equal(WaitingReason.NewFeedbackDuringRevision, outcome.State.WaitingReason);
        Assert.Contains("Revision.", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult, StringComparison.Ordinal);
        Assert.Equal(0, git.PushCallCount);
    }

    [Fact]
    public async Task RunAsyncPausesWhenReviewFeedbackArrivesDuringLfsUpload()
    {
        var state = await SeedReviewStateAsync();
        git.LfsRequired = true;
        git.OnLfsUpload = () => provider.MergeRequestComments[(Repository.Id, 1)] =
        [
            new ProviderComment(
                9,
                "bob",
                "Please account for this new feedback.",
                clock.UtcNow.AddMinutes(1),
                clock.UtcNow.AddMinutes(1),
                new AttachmentSource("merge-request-comment", "9"),
                false),
        ];
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Revising, outcome.State.Phase);
        Assert.Equal(WaitingReason.NewFeedbackDuringRevision, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
        Assert.Contains("Revision.", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).ImplementationResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncStartsNewRevisionForNewFeedbackInsteadOfPublishingRetainedResult()
    {
        var state = await SeedReviewStateAsync();
        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        var firstOmp = new FakeOmpClient().EnqueueRun(
            onStart: () => provider.MergeRequestComments[(Repository.Id, 1)] =
            [
                new ProviderComment(9, "bob", "Please account for this new feedback.", clock.UtcNow.AddMinutes(1), clock.UtcNow.AddMinutes(1),
                    new AttachmentSource("merge-request-comment", "9"), false),
            ],
            new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"First revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var paused = await workflow.RunAsync(CreateConfig(), 1, state, firstOmp, CancellationToken.None);
        var secondOmp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Feedback revision.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await workflow.RunAsync(CreateConfig(), 1, paused.State, secondOmp, CancellationToken.None);

        Assert.Equal(WorkflowPhase.Review, outcome.State.Phase);
        Assert.Single(secondOmp.RunRequests);
        Assert.Contains("Please account for this new feedback.", secondOmp.RunRequests[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("Feedback revision.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncRecoveryTreatsThreadResolutionChangeAfterPublicationAsNewFeedback()
    {
        var state = await SeedReviewStateAsync();
        provider.ReviewThreads[(Repository.Id, 1)] =
        [
            new ProviderReviewThread(
                "thread-1",
                IsResolved: true,
                [
                    new ProviderComment(
                        2,
                        "carol",
                        "Please handle this review concern.",
                        clock.UtcNow,
                        clock.UtcNow,
                        new AttachmentSource("review-thread-comment", "2"),
                        false),
                ]),
        ];
        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));
        await workflow.RunAsync(
            CreateConfig(),
            1,
            state,
            new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
                "session-1",
                clock.UtcNow,
                """{"summary":"Addressed review concern.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}""")),
            CancellationToken.None);

        var publishedCheckpoint = CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body);
        var publishedState = CanonicalStateSerializer.ToWorkflowState(publishedCheckpoint.State);
        Assert.Contains("thread:thread-1:resolved=True", publishedState.ReviewFeedbackVersions!);
        provider.ReviewThreads[(Repository.Id, 1)] =
        [
            new ProviderReviewThread(
                "thread-1",
                IsResolved: false,
                [
                    new ProviderComment(
                        2,
                        "carol",
                        "Please handle this review concern.",
                        clock.UtcNow,
                        clock.UtcNow,
                        new AttachmentSource("review-thread-comment", "2"),
                        false),
                ]),
        ];
        var recoveryState = publishedState with
        {
            Phase = WorkflowPhase.Revising,
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
        };
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var content = CanonicalCommentMarkdown.Parse(canonical.Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(content with
            {
                State = CanonicalStateSerializer.ToDocument(recoveryState, content.State.PullOrMergeRequest),
            }),
            CancellationToken.None);

        var resumedOmp = new FakeOmpClient();
        var outcome = await workflow.RunAsync(
            CreateConfig(),
            1,
            recoveryState,
            resumedOmp,
            CancellationToken.None,
            publishRetainedResult: true);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.NewFeedbackDuringRevision, outcome.State.WaitingReason);
        Assert.Empty(resumedOmp.RunRequests);
        Assert.Equal(1, git.PushCallCount);
    }

    [Fact]
    public async Task RunAsyncFailsClosedWhenReviewFeedbackCheckpointIsMissing()
    {
        var state = await SeedReviewStateAsync();
        var canonical = Assert.Single(provider.IssueComments[(Repository.Id, 1)]);
        var content = CanonicalCommentMarkdown.Parse(canonical.Body);
        await provider.UpdateIssueCommentAsync(
            Repository,
            1,
            canonical.Id,
            CanonicalCommentMarkdown.Render(content with { State = content.State with { ReviewFeedbackCutoff = null } }),
            CancellationToken.None);
        var omp = new FakeOmpClient();

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.CorruptState, outcome.State.WaitingReason);
        Assert.Empty(omp.RunRequests);
        Assert.Contains("feedback checkpoint", Assert.Single(notifier.Notifications).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncContinueAfterMaterialDeviationPublishesRetainedRevisionWithoutRerunningOmp()
    {
        var state = await SeedReviewStateAsync();
        var materialDeviation = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Needs architecture change.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["Requires a new service boundary."],"risks":[],"isMaterialDeviation":true,"materialDeviationExplanation":"The approved plan cannot safely support the required service boundary."}"""));
        var workflow = new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

        var paused = await workflow.RunAsync(CreateConfig(), 1, state, materialDeviation, CancellationToken.None);
        var continued = await workflow.RunAsync(
            CreateConfig(), 1, paused.State, new FakeOmpClient(), CancellationToken.None, publishRetainedResult: true);

        Assert.Equal(WorkflowPhase.Review, continued.State.Phase);
        Assert.Equal(WaitingReason.ReviewRequested, continued.State.WaitingReason);
        Assert.Equal(1, git.PushCallCount);
        Assert.Contains("Needs architecture change.", provider.UpdatedComments[^1].Body, StringComparison.Ordinal);
    }
    [Fact]
    public async Task RunAsyncPersistsProtectedBranchWhenRevisionPublicationIsRejected()
    {
        var state = await SeedReviewStateAsync();
        git.PushException = new InvalidOperationException("Push rejected: protected branch hook declined.");
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Addressed review concern.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WorkflowPhase.Revising, outcome.State.Phase);
        Assert.Equal(WaitingReason.ProtectedBranch, outcome.State.WaitingReason);
        Assert.Equal(WorkflowNotificationKind.HumanActionRequired, Assert.Single(notifier.Notifications).Kind);
        Assert.Contains("branch protection", Assert.Single(notifier.Notifications).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("protected-branch", CanonicalCommentMarkdown.Parse(provider.UpdatedComments[^1].Body).State.WaitingReason);
    }
    [Fact]
    public async Task RunAsyncPausesBeforePushWhenLinkedRequestIsRetargetedDuringLfsUpload()
    {
        var state = await SeedReviewStateAsync();
        git.LfsRequired = true;
        git.OnLfsUpload = () => provider.MergeRequests[1] = provider.MergeRequests[1] with
        {
            TargetBranch = "retargeted",
        };
        var omp = new FakeOmpClient().EnqueueRun(new OmpCompletedEvent(
            "session-1",
            clock.UtcNow,
            """{"summary":"Addressed review concern.","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, omp, CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Waiting, outcome.Status);
        Assert.Equal(WaitingReason.ManualIntervention, outcome.State.WaitingReason);
        Assert.Equal(0, git.PushCallCount);
    }

    [Fact]
    public async Task RunAsyncCompletesInsteadOfRevisingMergedRequest()
    {
        var state = await SeedReviewStateAsync();
        provider.MergeRequests[1] = provider.MergeRequests[1] with { IsMerged = true };

        var outcome = await new RevisionWorkflow(new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock))
            .RunAsync(CreateConfig(), 1, state, new FakeOmpClient(), CancellationToken.None);

        Assert.Equal(WorkflowOutcomeStatus.Progressed, outcome.Status);
        Assert.Equal(WorkflowPhase.Done, outcome.State.Phase);
        Assert.Empty(git.PublishedSubmoduleBaseCommits);
        Assert.Equal(0, git.PushCallCount);
    }



    private async Task<WorkflowState> SeedReviewStateAsync()
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var workflowId = WorkflowId.New();
        var state = new WorkflowState(
            workflowId, WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested,
            1, 1, "session-1", "agent/issue-1-bug", "main", "abc123", clock.UtcNow,
            ReviewFeedbackCutoff: clock.UtcNow.AddHours(-1),
            ExpectedImplementationHead: git.BranchCommitToReturn);
        var document = CanonicalStateSerializer.ToDocument(state, "github/octo/widgets#1");
        var content = new CanonicalCommentContent("Plan text.", ["Decision."], "Initial implementation summary.", document);
        await provider.CreateIssueCommentAsync(Repository, 1, CanonicalCommentMarkdown.Render(content), CancellationToken.None);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] = ["agent:phase:review", "agent:state:waiting", "agent:cmd:revise"];
        provider.MergeRequests[1] = new ProviderMergeRequest(Repository, 1, state.Branch, state.TargetBranch, "Bug", "body", true, false, false, new AttachmentSource("merge-request-description", "1"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, workflowId.ToString(), "worktree"));
        return state;
    }
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
}
