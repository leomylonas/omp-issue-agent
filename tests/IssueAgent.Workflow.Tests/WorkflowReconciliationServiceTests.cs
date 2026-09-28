using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class WorkflowReconciliationServiceTests : IDisposable
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "issueagent-reconciliation-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider provider = new();
    private readonly FakeGitRepositoryManager git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly FixedClock clock = new(DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task ConsistentWaitingStateAllowsCommandDispatchWithoutRemoteMutation()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.ResumeAllowed, result.Disposition);
        Assert.Equal(state, result.State);
        Assert.Empty(provider.UpdatedComments);
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task InitialPlanningBootstrapCheckpointResumesWithoutRecreatingItsWorktree()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planning, WorkflowOperationalState.Working, waitingReason: null);
        Directory.Delete(WorktreePath(state), recursive: true);
        state = state with { PlanRevision = 0, ApprovedPlanRevision = null, OmpSessionId = string.Empty, OmpSessionFile = null };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(new CanonicalCommentContent(
                "Planning is in progress.",
                [],
                null,
                CanonicalStateSerializer.ToDocument(state, pullOrMergeRequest: null))),
        };

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.ResumeAllowed, result.Disposition);
        Assert.Equal(state, result.State);
        Assert.False(Directory.Exists(WorktreePath(state)));
        Assert.Empty(provider.UpdatedComments);
    }
    [Fact]
    public async Task CancellationCommandIsPreservedDuringInitialPlanningBootstrapReconciliation()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planning, WorkflowOperationalState.Working, waitingReason: null);
        Directory.Delete(WorktreePath(state), recursive: true);
        state = state with { PlanRevision = 0, ApprovedPlanRevision = null, OmpSessionId = string.Empty, OmpSessionFile = null };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(new CanonicalCommentContent(
                "Planning is in progress.",
                [],
                null,
                CanonicalStateSerializer.ToDocument(state, pullOrMergeRequest: null))),
        };
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            [WorkflowLabels.PlanningPhase, WorkflowLabels.WorkingState, WorkflowCommandLabels.Cancel];

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.ResumeAllowed, result.Disposition);
        Assert.Contains(WorkflowCommandLabels.Cancel, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.False(Directory.Exists(WorktreePath(state)));
    }


    [Fact]
    public async Task AmbiguousCommandsBlockInitialPlanningBootstrapBeforeWorktreeRecovery()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planning, WorkflowOperationalState.Working, waitingReason: null);
        Directory.Delete(WorktreePath(state), recursive: true);
        state = state with { PlanRevision = 0, ApprovedPlanRevision = null, OmpSessionId = string.Empty, OmpSessionFile = null };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(new CanonicalCommentContent(
                "Planning is in progress.",
                [],
                null,
                CanonicalStateSerializer.ToDocument(state, pullOrMergeRequest: null))),
        };
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            [WorkflowLabels.PlanningPhase, WorkflowLabels.WorkingState, WorkflowCommandLabels.Cancel, WorkflowCommandLabels.Continue];

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.AmbiguousCommand, result.State!.WaitingReason);
        Assert.False(Directory.Exists(WorktreePath(state)));
        Assert.Contains(WorkflowLabels.WaitingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain(WorkflowLabels.WorkingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
    }

    [Fact]
    public async Task PersistedWorkingStateIsPausedAndEscalatedWithoutDeletingWorktree()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Implementing, WorkflowOperationalState.Working, waitingReason: null);
        var worktreePath = WorktreePath(state);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.ManualIntervention, result.State!.WaitingReason);
        Assert.Equal(
            WorkflowOperationalState.Working,
            CanonicalStateSerializer.ToWorkflowState(result.Content!.State).OperationalState);
        Assert.True(Directory.Exists(worktreePath));
        Assert.Contains("agent:state:waiting", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Single(provider.UpdatedComments);
        Assert.Equal(WorkflowNotificationKind.HumanActionRequired, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task RepeatedWaitingPauseRetriesLabelTransitionAfterLabelFailure()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Implementing, WorkflowOperationalState.Waiting, WaitingReason.ManualIntervention);
        var labels = provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)];
        labels.Remove(WorkflowLabels.WaitingState);
        labels.Add(WorkflowLabels.WorkingState);
        provider.AddLabelsFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().PauseForHumanAsync(
            CreateConfig(),
            1,
            canonical,
            CanonicalCommentMarkdown.Parse(canonical.Body),
            state,
            WaitingReason.ManualIntervention,
            "The retained workflow requires human review.",
            CancellationToken.None));

        var result = await CreateService().PauseForHumanAsync(
            CreateConfig(),
            1,
            canonical,
            CanonicalCommentMarkdown.Parse(canonical.Body),
            state,
            WaitingReason.ManualIntervention,
            "The retained workflow requires human review.",
            CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Contains(WorkflowLabels.WaitingState, labels);
        Assert.DoesNotContain(WorkflowLabels.WorkingState, labels);
        Assert.Empty(provider.UpdatedComments);
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task MissingRemoteRevisionBlockerIsPersistedOnceAndDoesNotRepeatNotifications()
    {
        var (state, canonical) = SeedWorkflow(
            WorkflowPhase.Revising,
            WorkflowOperationalState.Waiting,
            WaitingReason.RemoteHistoryRewrite);
        state = state with
        {
            ReviewFeedbackCutoff = clock.UtcNow.AddHours(-1),
            ReviewFeedbackVersions = new HashSet<string>(),
        };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(
                CanonicalCommentMarkdown.Parse(canonical.Body) with
                {
                    State = CanonicalStateSerializer.ToDocument(state, pullOrMergeRequest: null),
                }),
        };
        provider.IssueComments[(Repository.Id, 1)][0] = canonical;
        const string explanation =
            "The revision branch disappeared from the authoritative remote before the retained revision could be rebuilt. Restore the branch and continue again.";

        var first = await CreateService().PauseForHumanAsync(
            CreateConfig(),
            1,
            canonical,
            CanonicalCommentMarkdown.Parse(canonical.Body),
            state,
            WaitingReason.MissingRemoteRevisionBranch,
            explanation,
            CancellationToken.None);

        var persistedComment = provider.IssueComments[(Repository.Id, 1)].Single();
        var persistedContent = CanonicalCommentMarkdown.Parse(persistedComment.Body);
        var persistedState = CanonicalStateSerializer.ToWorkflowState(persistedContent.State);
        git.RemoteBranchCommitToReturn = null;
        var repeated = await CreateService().ReconcileAsync(
            CreateConfig(),
            1,
            persistedComment,
            CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, first.Disposition);
        Assert.Equal(ReconciliationDisposition.Waiting, repeated.Disposition);
        Assert.Equal(WaitingReason.MissingRemoteRevisionBranch, persistedState.WaitingReason);
        Assert.Equal(clock.UtcNow, persistedState.UpdatedAt);
        Assert.Single(provider.UpdatedComments);
        Assert.Equal(explanation, Assert.Single(notifier.Notifications).Message);
    }

    [Fact]
    public async Task InterruptedPlanningRecordsItsOriginalPhaseBeforeWaiting()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planning, WorkflowOperationalState.Working, waitingReason: null);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("planning", persisted.State.InterruptedPhase);
        Assert.Equal(WorkflowOperationalState.Waiting, CanonicalStateSerializer.ToWorkflowState(persisted.State).OperationalState);
    }

    [Fact]
    public async Task DivergedRemoteBranchIsPausedAndLocalStateIsPreserved()
    {
        git.BranchCommitToReturn = "local-sha";
        git.RemoteBranchCommitToReturn = "remote-sha";
        git.RemoteBranchIsDescendant = false;
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Implementing, WorkflowOperationalState.Waiting, WaitingReason.ManualIntervention);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.RemoteHistoryRewrite, result.State!.WaitingReason);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Single(provider.UpdatedComments);
    }
    [Theory]
    [InlineData(WaitingReason.MissingCredentials)]
    [InlineData(WaitingReason.ProtectedBranch)]
    public async Task RevisionPublicationBlockerRetainsLocalAheadResult(
        WaitingReason publicationBlocker)
    {
        git.BranchCommitToReturn = "local-revision";
        git.RemoteBranchCommitToReturn = "published-revision";
        git.IsAncestor = (ancestor, descendant) =>
            ancestor == "published-revision" && descendant == "local-revision";
        var (state, canonical) = SeedWorkflow(
            WorkflowPhase.Revising,
            WorkflowOperationalState.Waiting,
            publicationBlocker);
        state = state with
        {
            ReviewFeedbackCutoff = clock.UtcNow.AddHours(-1),
            ReviewFeedbackVersions = new HashSet<string>(),
        };
        var content = CanonicalCommentMarkdown.Parse(canonical.Body) with
        {
            ImplementationResult = "Locally committed revision.",
            State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#7"),
        };
        canonical = canonical with { Body = CanonicalCommentMarkdown.Render(content) };
        provider.IssueComments[(Repository.Id, 1)][0] = canonical;

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(publicationBlocker, result.State!.WaitingReason);
        Assert.Equal("Locally committed revision.", result.Content!.ImplementationResult);
        Assert.Empty(provider.UpdatedComments);
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task InterruptedRevisionCheckpointRetainsLocalAheadResultForManualRecovery()
    {
        git.BranchCommitToReturn = "local-revision";
        git.RemoteBranchCommitToReturn = "published-revision";
        git.IsAncestor = (ancestor, descendant) =>
            ancestor == "published-revision" && descendant == "local-revision";
        var (state, canonical) = SeedWorkflow(
            WorkflowPhase.Revising,
            WorkflowOperationalState.Working,
            waitingReason: null);
        state = state with
        {
            ReviewFeedbackCutoff = clock.UtcNow.AddHours(-1),
            ReviewFeedbackVersions = new HashSet<string>(),
        };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(CanonicalCommentMarkdown.Parse(canonical.Body) with
            {
                ImplementationResult = "Checkpointed revision result.",
                State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#7"),
            }),
        };
        provider.IssueComments[(Repository.Id, 1)][0] = canonical;

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.ManualIntervention, result.State!.WaitingReason);
        Assert.Empty(git.ResetWorktrees);
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("Checkpointed revision result.", persisted.ImplementationResult);
        Assert.Equal("manual-intervention", persisted.State.WaitingReason);
        Assert.Equal("revising", persisted.State.InterruptedPhase);
    }

    [Fact]
    public async Task ManualRevisionRecoveryRetainsCheckpointedLocalAheadResult()
    {
        git.BranchCommitToReturn = "local-revision";
        git.RemoteBranchCommitToReturn = "published-revision";
        git.IsAncestor = (ancestor, descendant) =>
            ancestor == "published-revision" && descendant == "local-revision";
        var (state, canonical) = SeedWorkflow(
            WorkflowPhase.Revising,
            WorkflowOperationalState.Waiting,
            WaitingReason.ManualIntervention);
        state = state with
        {
            InterruptedPhase = WorkflowPhase.Revising,
            ReviewFeedbackCutoff = clock.UtcNow.AddHours(-1),
            ReviewFeedbackVersions = new HashSet<string>(),
        };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(CanonicalCommentMarkdown.Parse(canonical.Body) with
            {
                ImplementationResult = "Checkpointed revision result.",
                State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#7"),
            }),
        };
        provider.IssueComments[(Repository.Id, 1)][0] = canonical;

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.ManualIntervention, result.State!.WaitingReason);
        Assert.Equal("Checkpointed revision result.", result.Content!.ImplementationResult);
        Assert.Empty(git.ResetWorktrees);
        Assert.Empty(provider.UpdatedComments);
        Assert.Empty(notifier.Notifications);
    }



    [Fact]
    public async Task AcceptRemoteHistoryPersistsAcceptedHeadAndRestoresPlanApproval()
    {
        var (state, canonical) = SeedWorkflow(
            WorkflowPhase.Planned,
            WorkflowOperationalState.Waiting,
            WaitingReason.RemoteHistoryRewrite);
        var content = CanonicalCommentMarkdown.Parse(canonical.Body) with
        {
            ImplementationResult = "Uncheckpointed local result.",
        };

        var accepted = await CreateService().AcceptRemoteHistoryAsync(
            CreateConfig(),
            1,
            canonical,
            content,
            state,
            "accepted-remote-head",
            CancellationToken.None);

        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal(WorkflowPhase.Planned, accepted.Phase);
        Assert.Equal(WaitingReason.PlanApproval, accepted.WaitingReason);
        Assert.Equal("accepted-remote-head", accepted.BaseCommit);
        Assert.Equal("Implement the fix.", persisted.PlanText);
        Assert.Null(persisted.ImplementationResult);
        Assert.Equal("accepted-remote-head", persisted.State.BaseCommit);
        Assert.Equal("plan-approval", persisted.State.WaitingReason);
        Assert.Contains(WorkflowLabels.PlannedPhase, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Contains(WorkflowLabels.WaitingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
    }
    [Fact]
    public async Task AcceptRemoteHistoryPreservesPublishedReviewState()
    {
        var (state, canonical) = SeedWorkflow(
            WorkflowPhase.Review,
            WorkflowOperationalState.Waiting,
            WaitingReason.RemoteHistoryRewrite);
        state = state with
        {
            ReviewFeedbackCutoff = clock.UtcNow.AddHours(-1),
            ReviewFeedbackVersions = new HashSet<string> { "comment:1:version" },
            ExpectedImplementationHead = "feedface",
            PublicationStage = ImplementationPublicationStage.BranchPublished,
        };
        var content = CanonicalCommentMarkdown.Parse(canonical.Body) with
        {
            ImplementationResult = "Published implementation result.",
            State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#7"),
        };

        var accepted = await CreateService().AcceptRemoteHistoryAsync(
            CreateConfig(), 1, canonical, content, state, "accepted-remote-head", CancellationToken.None);

        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal(WorkflowPhase.Review, accepted.Phase);
        Assert.Equal(WaitingReason.ReviewRequested, accepted.WaitingReason);
        Assert.Equal("Published implementation result.", persisted.ImplementationResult);
        Assert.Equal("accepted-remote-head", persisted.State.BaseCommit);
        Assert.Equal("accepted-remote-head", accepted.ExpectedImplementationHead);

        Assert.Equal(state.ReviewFeedbackCutoff, persisted.State.ReviewFeedbackCutoff);
        Assert.Equal(state.ReviewFeedbackVersions!.Order(), persisted.State.ReviewFeedbackVersions!);
        Assert.Contains(WorkflowLabels.ReviewPhase, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
    }


    [Fact]
    public async Task FastForwardReconciliationAdoptsVerifiedPublishedWorkflow()
    {
        git.BranchCommitToReturn = "abc123";
        git.RemoteBranchCommitToReturn = "deadbeef";
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);
        state = state with
        {
            ReviewFeedbackCutoff = clock.UtcNow.AddHours(-1),
            ExpectedImplementationHead = "abc123",
            PublicationStage = ImplementationPublicationStage.BranchPublished,
        };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(CanonicalCommentMarkdown.Parse(canonical.Body) with
            {
                State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#7"),
            }),
        };
        provider.IssueComments[(Repository.Id, 1)][0] = canonical;

        var reconciled = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.ResumeAllowed, reconciled.Disposition);
        Assert.Equal("deadbeef", reconciled.State!.BaseCommit);
        Assert.Equal(
            "deadbeef",
            CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body).State.BaseCommit);
        Assert.Equal([(Repository.Id, WorktreePath(reconciled.State!), "deadbeef")], git.ResetWorktrees);
    }
    [Fact]
    public async Task FastForwardReconciliationAdoptsRemoteDescendantForPlannedPublishedWorkflow()
    {
        git.BranchCommitToReturn = "abc123";
        git.RemoteBranchCommitToReturn = "deadbeef";
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval);
        state = state with
        {
            BaseCommit = "feedface",
            ExpectedImplementationHead = "feedface",
            PublicationStage = ImplementationPublicationStage.BranchPublished,
        };
        canonical = canonical with
        {
            Body = CanonicalCommentMarkdown.Render(CanonicalCommentMarkdown.Parse(canonical.Body) with
            {
                ImplementationResult = "Published implementation.",
                State = CanonicalStateSerializer.ToDocument(state, $"{Repository.Id}#7"),
            }),
        };
        provider.IssueComments[(Repository.Id, 1)][0] = canonical;

        var reconciled = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.ResumeAllowed, reconciled.Disposition);
        Assert.Equal("deadbeef", reconciled.State!.BaseCommit);
        Assert.Equal("deadbeef", reconciled.State.ExpectedImplementationHead);
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("deadbeef", persisted.State.BaseCommit);
        Assert.Equal("deadbeef", persisted.State.ExpectedImplementationHead);
        Assert.Equal($"{Repository.Id}#7", persisted.State.PullOrMergeRequest);
        Assert.Equal("Published implementation.", persisted.ImplementationResult);
    }


    [Fact]
    public async Task FastForwardReconciliationDoesNotAdoptUnpublishedWorkflow()
    {
        git.BranchCommitToReturn = "abc123";
        git.RemoteBranchCommitToReturn = "deadbeef";
        var (_, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval);

        var reconciled = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, reconciled.Disposition);
        Assert.Equal(WaitingReason.RemoteHistoryRewrite, reconciled.State!.WaitingReason);
        Assert.Empty(git.ResetWorktrees);
    }
    [Fact]
    public async Task MergedRequestTransitionsDoneAndCleansOnlyLocalState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);
        provider.MergeRequests[7] = new ProviderMergeRequest(
            Repository,
            7,
            state.Branch,
            state.TargetBranch,
            "Fix",
            $"<!-- issue-agent:workflow:{state.WorkflowId} -->",
            IsDraft: false,
            IsMerged: true,
            IsClosed: true,
            new AttachmentSource("merge-request-description", "7"));

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Completed, result.Disposition);
        Assert.Equal(WorkflowPhase.Done, result.State!.Phase);
        Assert.False(Directory.Exists(WorktreePath(state)));
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("done", persisted.State.Phase);
    }

    [Fact]
    public async Task ClosedUnmergedRequestTransitionsCancelledAndCleansOnlyLocalState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);
        provider.MergeRequests[7] = new ProviderMergeRequest(
            Repository,
            7,
            state.Branch,
            state.TargetBranch,
            "Fix",
            $"<!-- issue-agent:workflow:{state.WorkflowId} -->",
            IsDraft: false,
            IsMerged: false,
            IsClosed: true,
            new AttachmentSource("merge-request-description", "7"));

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Completed, result.Disposition);
        Assert.Equal(WorkflowPhase.Cancelled, result.State!.Phase);
        Assert.False(Directory.Exists(WorktreePath(state)));
        var persisted = CanonicalCommentMarkdown.Parse(Assert.Single(provider.UpdatedComments).Body);
        Assert.Equal("cancelled", persisted.State.Phase);
    }

    [Fact]
    public async Task TerminalRequestWithMismatchedStoredIdentityPausesWithoutCleaningLocalState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);
        provider.MergeRequests[8] = new ProviderMergeRequest(
            Repository,
            8,
            state.Branch,
            state.TargetBranch,
            "Unrelated fix",
            $"<!-- issue-agent:workflow:{state.WorkflowId} -->",
            IsDraft: false,
            IsMerged: true,
            IsClosed: true,
            new AttachmentSource("merge-request-description", "8"));

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.ManualIntervention, result.State!.WaitingReason);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Contains("identity", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalRequestWithoutWorkflowMarkerPausesWithoutCleaningLocalState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);
        provider.MergeRequests[7] = new ProviderMergeRequest(
            Repository,
            7,
            state.Branch,
            state.TargetBranch,
            "Unrelated fix",
            "A human-created merge request on the same branch.",
            IsDraft: false,
            IsMerged: true,
            IsClosed: true,
            new AttachmentSource("merge-request-description", "7"));

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.ManualIntervention, result.State!.WaitingReason);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Contains("marker", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewWithoutFeedbackCheckpointPausesRatherThanGuessingWhichFeedbackWasProcessed()
    {
        var (_, canonical) = SeedWorkflow(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.ReviewRequested);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.CorruptState, result.State!.WaitingReason);
        Assert.Contains("feedback checkpoint", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousLabelsPauseWithoutDeletingRetainedState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowLabels.ReviewPhase);
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)].Add(WorkflowLabels.WorkingState);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.AmbiguousCommand, result.State!.WaitingReason);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Single(provider.UpdatedComments);
        Assert.Contains(WorkflowLabels.PlannedPhase, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Contains(WorkflowLabels.ReviewPhase, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Contains(WorkflowLabels.WaitingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Contains(WorkflowLabels.WorkingState, provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.Single(notifier.Notifications);
    }

    [Fact]
    public async Task CorruptCanonicalStateEscalatesWithoutDeletingRetainedState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Working, waitingReason: null);
        var corrupt = canonical with { Body = CanonicalCommentMarkdown.StateLocatorMarker };

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, corrupt, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Corrupt, result.Disposition);
        Assert.True(Directory.Exists(WorktreePath(state)));
        Assert.Contains("agent:state:waiting", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        Assert.DoesNotContain("agent:state:working", provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)]);
        var update = Assert.Single(provider.UpdatedComments);
        Assert.Contains("IssueAgent paused", update.Body, StringComparison.Ordinal);
        Assert.Equal(WorkflowNotificationKind.HumanActionRequired, Assert.Single(notifier.Notifications).Kind);
    }

    [Fact]
    public async Task TerminalReconciliationRetriesLabelCleanupBeforeRemovingLocalState()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Done, WorkflowOperationalState.Waiting, WaitingReason.ManualIntervention);
        var labels = provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)];
        labels.Remove(WorkflowLabels.Phase(WorkflowPhase.Done));
        labels.Add(WorkflowLabels.Phase(WorkflowPhase.Implementing));
        labels.Add(WorkflowLabels.WorkingState);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Completed, result.Disposition);
        Assert.Equal([WorkflowLabels.Phase(WorkflowPhase.Done)], labels);
        Assert.Contains(WorkflowLabels.Phase(WorkflowPhase.Done), provider.CreatedLabels);
    }

    [Fact]
    public async Task RecreatedMissingWorktreePausesForExplicitHumanReview()
    {
        var (state, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval);
        Directory.Delete(Path.Combine(workspaceRoot, state.WorkflowId.ToString()), recursive: true);

        var result = await CreateService().ReconcileAsync(CreateConfig(), 1, canonical, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Waiting, result.Disposition);
        Assert.Equal(WaitingReason.ManualIntervention, result.State!.WaitingReason);
        Assert.Single(git.CreatedWorktrees);
        Assert.Contains("recreated", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptRecoveryEnsuresTheFullManagedLabelCatalog()
    {
        var (_, canonical) = SeedWorkflow(WorkflowPhase.Planned, WorkflowOperationalState.Working, waitingReason: null);
        var corrupt = canonical with { Body = CanonicalCommentMarkdown.StateLocatorMarker };

        _ = await CreateService().ReconcileAsync(CreateConfig(), 1, corrupt, CancellationToken.None);

        Assert.All(LabelCatalog.All, label => Assert.Contains(label.Name, provider.CreatedLabels));
    }

    private (WorkflowState State, ProviderComment Canonical) SeedWorkflow(
        WorkflowPhase phase,
        WorkflowOperationalState operationalState,
        WaitingReason? waitingReason)
    {
        provider.AddIssue(Repository, 1, "Bug", "Description");
        var state = new WorkflowState(
            WorkflowId.New(),
            phase,
            operationalState,
            waitingReason,
            1,
            1,
            "session-1",
            "agent/issue-1-bug",
            "main",
            "abc123",
            clock.UtcNow.AddHours(-1),
            PlanInputHash: PlanInputHasher.Compute("Bug", "Description"));
        Directory.CreateDirectory(WorktreePath(state));
        provider.Labels[(Repository.Id, ProviderWorkItemKind.Issue, 1)] =
            [WorkflowLabels.Phase(phase), WorkflowLabels.State(operationalState)];
        var content = new CanonicalCommentContent(
            "Implement the fix.",
            ["Keep the change focused."],
            null,
            CanonicalStateSerializer.ToDocument(state, phase == WorkflowPhase.Review ? $"{Repository.Id}#7" : null));
        provider.AddComment(Repository, 1, "issue-agent", CanonicalCommentMarkdown.Render(content), clock.UtcNow, isBot: true);
        return (state, provider.IssueComments[(Repository.Id, 1)].Single());
    }

    private WorkflowReconciliationService CreateService() => new(
        new WorkflowDependencies(provider, git, CreateContextBuilder(), notifier, clock));

    private AgentContextBuilder CreateContextBuilder() => new(
        provider,
        new AttachmentPipeline(provider, new AttachmentLimits()),
        new AgentContextBuilderOptions());

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

    private string WorktreePath(WorkflowState state) =>
        Path.Combine(workspaceRoot, state.WorkflowId.ToString(), "worktree");

    public void Dispose()
    {
        if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
    }
}
