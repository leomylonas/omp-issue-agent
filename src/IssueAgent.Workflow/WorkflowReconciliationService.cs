using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

public enum ReconciliationDisposition
{
    ResumeAllowed,
    Waiting,
    Completed,
    Corrupt,
}

public sealed record WorkflowReconciliationResult(
    ReconciliationDisposition Disposition,
    WorkflowState? State,
    CanonicalCommentContent? Content,
    ProviderComment CanonicalComment,
    string Explanation,
    ProviderMergeRequest? MergeRequest = null);

/// <summary>Reconstructs the durable workflow snapshot from provider and local Git state before an
/// existing workflow is resumed. Unsafe or ambiguous state is preserved and escalated instead of
/// being guessed at.</summary>
public sealed class WorkflowReconciliationService(WorkflowDependencies dependencies)
{
    private const string CorruptionWarningMarker = "<!-- issue-agent:corruption-warning -->";

    public async Task<WorkflowReconciliationResult> ReconcileAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderComment canonicalComment,
        CancellationToken cancellationToken)
    {
        CanonicalCommentContent content;
        WorkflowState state;
        try
        {
            content = CanonicalCommentMarkdown.Parse(canonicalComment.Body);
            state = CanonicalStateSerializer.ToWorkflowState(content.State);
            state.EnsureValid();
        }
        catch (Exception exception) when (exception is CanonicalCommentCorruptException or CanonicalStateException or InvalidOperationException)
        {
            var explanation = $"Canonical workflow state is corrupt and could not be reconstructed safely ({exception.GetType().Name}).";
            await EscalateCorruptionAsync(
                config,
                issueNumber,
                canonicalComment,
                explanation,
                cancellationToken).ConfigureAwait(false);
            return new WorkflowReconciliationResult(
                ReconciliationDisposition.Corrupt,
                State: null,
                Content: null,
                canonicalComment,
                explanation);
        }

        ProviderMergeRequest? mergeRequest;
        try
        {
            mergeRequest = await FindMergeRequestAsync(config, state, content, cancellationToken).ConfigureAwait(false);
        }
        catch (StoredMergeRequestIdentity.StoredMergeRequestUnavailableException exception)
        {
            return await PauseForHumanAsync(
                config,
                issueNumber,
                canonicalComment,
                content,
                state,
                WaitingReason.ManualIntervention,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CanonicalStateException exception)
        {
            var explanation = $"Canonical workflow state is corrupt and could not be reconstructed safely ({exception.GetType().Name}).";
            await EscalateCorruptionAsync(
                config,
                issueNumber,
                canonicalComment,
                explanation,
                cancellationToken).ConfigureAwait(false);
            return new WorkflowReconciliationResult(
                ReconciliationDisposition.Corrupt,
                State: null,
                Content: null,
                canonicalComment,
                explanation);
        }
        if (mergeRequest is not null && !HasWorkflowMarker(state, mergeRequest))
        {
            return await PauseForHumanAsync(
                config,
                issueNumber,
                canonicalComment,
                content,
                state,
                WaitingReason.ManualIntervention,
                "The stored or branch-matched PR/MR does not match this workflow's identity marker. Local workflow data was preserved for human review.",
                cancellationToken).ConfigureAwait(false);
        }
        if (mergeRequest is not null && content.State.PullOrMergeRequest is null)
        {
            // The branch was published before the canonical link checkpoint. Its workflow marker
            // is the durable ownership proof until the request number can be recorded.
            content = content with
            {
                State = content.State with { PullOrMergeRequest = $"{config.Repository.Id}#{mergeRequest.Number}" },
            };
            await PersistCanonicalStateAsync(config, issueNumber, canonicalComment, content, state, cancellationToken)
                .ConfigureAwait(false);
        }
        if (mergeRequest?.IsMerged == true)
        {
            var outcome = await new CancellationWorkflow(dependencies)
                .CompleteOnMergeAsync(config, issueNumber, state, content, cancellationToken)
                .ConfigureAwait(false);
            return new WorkflowReconciliationResult(ReconciliationDisposition.Completed, outcome.State, content, canonicalComment, outcome.Message!);
        }

        if (mergeRequest is { IsClosed: true, IsMerged: false })
        {
            var outcome = await new CancellationWorkflow(dependencies)
                .CompleteOnCloseWithoutMergeAsync(config, issueNumber, state, content, cancellationToken)
                .ConfigureAwait(false);
            return new WorkflowReconciliationResult(ReconciliationDisposition.Completed, outcome.State, content, canonicalComment, outcome.Message!);
        }

        if (state.Phase is WorkflowPhase.Done or WorkflowPhase.Cancelled)
        {
            var cancellation = new CancellationWorkflow(dependencies);
            await cancellation.ReconcileTerminalLabelsAsync(config, issueNumber, state, cancellationToken).ConfigureAwait(false);
            await cancellation.CleanupLocalStateAsync(config, state, cancellationToken).ConfigureAwait(false);
            return new WorkflowReconciliationResult(ReconciliationDisposition.Completed, state, content, canonicalComment, "Terminal workflow local state and labels are clean.");
        }
        var labels = await dependencies.Provider
            .GetLabelsAsync(new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber), cancellationToken)
            .ConfigureAwait(false);
        var labelSnapshot = LabelProtocol.Analyze(labels);
        if (IsInitialPlanningBootstrapCheckpoint(state))
        {
            var bootstrapDecision = ReconciliationDecider.Decide(new ReconciliationInput(
                labelSnapshot,
                content,
                LocalWorktreeExists: true,
                LocalWorktreeHeadCommit: null,
                RemoteBranchHeadCommit: null,
                PlanInputHashPresent: true));
            if (bootstrapDecision.Action == ReconciliationAction.WaitForHuman &&
                bootstrapDecision.Reason != WaitingReason.ManualIntervention)
            {
                return await PauseForHumanAsync(
                    config,
                    issueNumber,
                    canonicalComment,
                    content,
                    state,
                    bootstrapDecision.Reason!.Value,
                    bootstrapDecision.Explanation,
                    cancellationToken).ConfigureAwait(false);
            }

            return new WorkflowReconciliationResult(
                ReconciliationDisposition.ResumeAllowed,
                state,
                content,
                canonicalComment,
                "The initial-planning checkpoint is consistent and can safely resume.");
        }

        var worktreePath = Path.Combine(config.WorkflowsStoragePath, state.WorkflowId.ToString(), "worktree");
        var worktreeExists = Directory.Exists(worktreePath);
        if (!worktreeExists)
        {
            try
            {
                await dependencies.Git.CreateWorktreeAsync(
                    config.Repository.Id,
                    state.WorkflowId.ToString(),
                    worktreePath,
                    state.Branch,
                    state.BaseCommit,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await PauseForHumanAsync(
                    config,
                    issueNumber,
                    canonicalComment,
                    content,
                    state,
                    WaitingReason.CorruptState,
                    $"The retained worktree is missing and could not be recreated ({exception.GetType().Name}).",
                    cancellationToken).ConfigureAwait(false);
            }

            return await PauseForHumanAsync(
                config,
                issueNumber,
                canonicalComment,
                content,
                state,
                WaitingReason.ManualIntervention,
                "The retained worktree was missing and has been recreated from durable Git state. Review it before explicitly continuing.",
                cancellationToken).ConfigureAwait(false);
        }

        string? localHead = null;
        var worktreeDirty = false;
        if (worktreeExists)
        {
            localHead = await dependencies.Git.GetHeadCommitAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false);
            worktreeDirty = await dependencies.Git.HasUncommittedChangesAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false);
        }

        var remoteHead = await dependencies.Git
            .TryResolveRemoteBranchCommitAsync(config.Repository.Id, state.Branch, cancellationToken)
            .ConfigureAwait(false);
        var verifiedPublishedWorkflow = HasVerifiedPublishedCheckpoint(state, content);
        var localHeadIsAncestorOfRemote = verifiedPublishedWorkflow &&
            localHead is not null &&
            remoteHead is not null &&
            !string.Equals(localHead, remoteHead, StringComparison.Ordinal) &&
            await dependencies.Git.IsAncestorAsync(config.Repository.Id, localHead, remoteHead, cancellationToken).ConfigureAwait(false);
        if (HasRetainedRevisionResult(state, content) &&
            localHead is not null &&
            remoteHead is not null &&
            !worktreeDirty &&
            !localHeadIsAncestorOfRemote &&
            await dependencies.Git.IsAncestorAsync(
                config.Repository.Id, remoteHead, localHead, cancellationToken).ConfigureAwait(false))
        {
            if (state.OperationalState == WorkflowOperationalState.Working)
            {
                return await PauseForHumanAsync(
                    config,
                    issueNumber,
                    canonicalComment,
                    content,
                    state,
                    WaitingReason.ManualIntervention,
                    "A revision result was checkpointed before publication completed. The local revision is ahead of the remote branch and was retained for explicit human recovery.",
                    cancellationToken).ConfigureAwait(false);
            }

            return new WorkflowReconciliationResult(
                ReconciliationDisposition.Waiting,
                state,
                content,
                canonicalComment,
                "The local revision result is ahead of the remote branch after an interrupted publication. It was retained; review it and continue when ready.");
        }

        if (!worktreeDirty && localHeadIsAncestorOfRemote)
        {
            // Only a workflow with a verified published review request may adopt human commits
            // from its remote agent branch. An unpublished workflow must not treat an unrelated
            // remote ref as its implementation history.
            state = state with
            {
                BaseCommit = remoteHead!,
                ExpectedImplementationHead = remoteHead,
                UpdatedAt = dependencies.Clock.UtcNow,
            };
            content = content with
            {
                State = CanonicalStateSerializer.ToDocument(state, content.State.PullOrMergeRequest),
            };
            await PersistCanonicalStateAsync(
                config, issueNumber, canonicalComment, content, state, cancellationToken).ConfigureAwait(false);
            try
            {
                await dependencies.Git.ResetWorktreeAsync(config.Repository.Id, worktreePath, remoteHead!, cancellationToken).ConfigureAwait(false);
                await dependencies.Git.UpdateSubmodulesAsync(
                    config.Repository.Id,
                    worktreePath,
                    config.SubmoduleAuthenticationResolver ?? (_ => null),
                    cancellationToken).ConfigureAwait(false);
                await dependencies.Git.MaterializeLfsContentAsync(
                    config.Repository.Id,
                    worktreePath,
                    config.GitAuthentication,
                    config.SubmoduleAuthenticationResolver ?? (_ => null),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await PauseForHumanAsync(
                    config,
                    issueNumber,
                    canonicalComment,
                    content,
                    state,
                    WaitingReason.ManualIntervention,
                    $"The remote agent branch was adopted, but its submodules or LFS content could not be refreshed safely: {exception.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
            localHead = remoteHead;
            localHeadIsAncestorOfRemote = false;
        }
        var decision = ReconciliationDecider.Decide(new ReconciliationInput(
            labelSnapshot,
            content,
            worktreeExists,
            localHead,
            remoteHead,
            worktreeDirty,
            PlanInputHashPresent: content.State.PlanInputHash is { Length: > 0 },
            ReviewFeedbackProvenancePresent: state.Phase is not (WorkflowPhase.Review or WorkflowPhase.Revising) ||
                content.State.ReviewFeedbackCutoff is not null,
            LocalHeadIsAncestorOfRemote: localHeadIsAncestorOfRemote));

        if (state.WaitingReason == WaitingReason.MissingRemoteRevisionBranch &&
            decision.Reason == WaitingReason.RemoteHistoryRewrite)
        {
            return new WorkflowReconciliationResult(
                ReconciliationDisposition.Waiting,
                state,
                content,
                canonicalComment,
                "The revision branch remains unavailable on the authoritative remote. Restore the branch and continue again.");
        }

        if (decision.Action == ReconciliationAction.WaitForHuman)
        {
            return await PauseForHumanAsync(
                config,
                issueNumber,
                canonicalComment,
                content,
                state,
                decision.Reason!.Value,
                decision.Explanation,
                cancellationToken).ConfigureAwait(false);
        }

        return new WorkflowReconciliationResult(
            ReconciliationDisposition.ResumeAllowed,
            state,
            content,
            canonicalComment,
            decision.Explanation,
            mergeRequest);
    }

    private async Task<ProviderMergeRequest?> FindMergeRequestAsync(
        WorkflowRepositoryConfig config,
        WorkflowState state,
        CanonicalCommentContent content,
        CancellationToken cancellationToken)
    {
        var identity = content.State.PullOrMergeRequest;
        return identity is null
            ? await dependencies.Provider.FindMergeRequestAsync(
                config.Repository, state.Branch, state.TargetBranch, cancellationToken).ConfigureAwait(false)
            : await StoredMergeRequestIdentity.FindAsync(
                dependencies.Provider, config.Repository, identity, state.Branch, state.TargetBranch, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasWorkflowMarker(WorkflowState state, ProviderMergeRequest mergeRequest) =>
        mergeRequest.Description.Contains($"<!-- issue-agent:workflow:{state.WorkflowId} -->", StringComparison.Ordinal);

    private static bool HasRetainedRevisionResult(WorkflowState state, CanonicalCommentContent content) =>
        state.Phase == WorkflowPhase.Revising &&
        content.ImplementationResult is { Length: > 0 };
    private static bool HasVerifiedPublishedCheckpoint(WorkflowState state, CanonicalCommentContent content) =>
        state.PublicationStage == ImplementationPublicationStage.BranchPublished &&
        state.ExpectedImplementationHead is { Length: > 0 } &&
        content.State.PullOrMergeRequest is { Length: > 0 };


    private static bool IsInitialPlanningBootstrapCheckpoint(WorkflowState state) =>
        state.Phase == WorkflowPhase.Planning &&
        state.OperationalState == WorkflowOperationalState.Working &&
        state.PlanRevision == 0 &&
        string.IsNullOrEmpty(state.OmpSessionId) &&
        state.OmpSessionFile is null;


    /// <summary>Records a human's decision to treat a rewritten remote branch as authoritative.
    /// Unpublished work is discarded back to plan approval. A published review remains in review:
    /// its implementation result and feedback checkpoint still describe the remote branch humans
    /// are reviewing.</summary>
    public async Task<WorkflowState> AcceptRemoteHistoryAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderComment canonicalComment,
        CanonicalCommentContent content,
        WorkflowState state,
        string acceptedRemoteHead,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedRemoteHead);

        var preservesPublishedWorkflow = HasVerifiedPublishedCheckpoint(state, content);
        var preservesReview = preservesPublishedWorkflow && state.Phase == WorkflowPhase.Review;
        var acceptedState = state with
        {
            Phase = preservesReview ? WorkflowPhase.Review : WorkflowPhase.Planned,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = preservesReview ? WaitingReason.ReviewRequested : WaitingReason.PlanApproval,
            BaseCommit = acceptedRemoteHead,
            InterruptedPhase = null,
            ExpectedImplementationHead = preservesPublishedWorkflow ? acceptedRemoteHead : null,
            PublicationStage = preservesPublishedWorkflow ? state.PublicationStage : null,
            RebasedPublicationBase = preservesPublishedWorkflow ? state.RebasedPublicationBase : null,
            UpdatedAt = dependencies.Clock.UtcNow,
        };
        var acceptedContent = content with
        {
            ImplementationResult = preservesPublishedWorkflow ? content.ImplementationResult : null,
            State = CanonicalStateSerializer.ToDocument(acceptedState, content.State.PullOrMergeRequest),
        };
        await PersistCanonicalStateAsync(
            config, issueNumber, canonicalComment, acceptedContent, acceptedState, cancellationToken)
            .ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, acceptedState.Phase, cancellationToken).ConfigureAwait(false);
        return acceptedState;
    }

    public async Task<WorkflowReconciliationResult> PauseForHumanAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderComment canonicalComment,
        CanonicalCommentContent content,
        WorkflowState state,
        WaitingReason reason,
        string explanation,
        CancellationToken cancellationToken,
        bool recordNewBlocker = false)
    {
        var alreadyRecorded = !recordNewBlocker &&
            state.OperationalState == WorkflowOperationalState.Waiting &&
            state.WaitingReason == reason;
        var waitingState = state with
        {
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = reason,
            InterruptedPhase = state.OperationalState == WorkflowOperationalState.Working
                ? state.Phase
                : state.InterruptedPhase,
            UpdatedAt = alreadyRecorded ? state.UpdatedAt : dependencies.Clock.UtcNow,
        };
        if (!alreadyRecorded)
        {
            await PersistCanonicalStateAsync(config, issueNumber, canonicalComment, content, waitingState, cancellationToken)
                .ConfigureAwait(false);
        }

        if (reason == WaitingReason.AmbiguousCommand)
        {
            await AddWaitingLabelWithoutRepairingAmbiguousManagedLabelsAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await TransitionLabelsAsync(config, issueNumber, waitingState.Phase, cancellationToken).ConfigureAwait(false);
        }

        if (!alreadyRecorded)
        {
            await dependencies.Notifier.NotifyAsync(
                new WorkflowNotification(
                    WorkflowNotificationKind.HumanActionRequired,
                    config.Repository.Id,
                    issueNumber,
                    waitingState.WorkflowId.ToString(),
                    explanation),
                cancellationToken).ConfigureAwait(false);
        }

        return new WorkflowReconciliationResult(
            ReconciliationDisposition.Waiting,
            waitingState,
            content,
            canonicalComment,
            explanation);
    }

    private async Task AddWaitingLabelWithoutRepairingAmbiguousManagedLabelsAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber);
        var labels = await dependencies.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        var snapshot = LabelProtocol.Analyze(labels);
        var commandOnlyAmbiguity = snapshot.Ambiguity == LabelAmbiguity.MultipleCommandLabels &&
            !snapshot.IsPhaseStateAmbiguous;
        if (!labels.Contains(WorkflowLabels.WaitingState))
        {
            await dependencies.Provider.EnsureLabelAsync(
                config.Repository,
                LabelCatalog.All.First(label => label.Name == WorkflowLabels.WaitingState),
                cancellationToken).ConfigureAwait(false);
            await dependencies.Provider.AddLabelsAsync(workItem, [WorkflowLabels.WaitingState], cancellationToken).ConfigureAwait(false);
        }

        if (commandOnlyAmbiguity && labels.Contains(WorkflowLabels.WorkingState))
        {
            await dependencies.Provider.RemoveLabelAsync(workItem, WorkflowLabels.WorkingState, cancellationToken).ConfigureAwait(false);
        }

    }

    private async Task EscalateCorruptionAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderComment canonicalComment,
        string explanation,
        CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber);
        await LabelCatalog.EnsureAllAsync(dependencies.Provider, config.Repository, cancellationToken).ConfigureAwait(false);
        var labels = await dependencies.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        if (!labels.Contains(WorkflowLabels.WaitingState))
        {
            await dependencies.Provider.EnsureLabelAsync(
                config.Repository,
                LabelCatalog.All.First(label => label.Name == WorkflowLabels.WaitingState),
                cancellationToken).ConfigureAwait(false);
            await dependencies.Provider.AddLabelsAsync(workItem, [WorkflowLabels.WaitingState], cancellationToken).ConfigureAwait(false);
        }
        if (labels.Contains(WorkflowLabels.WorkingState))
        {
            await dependencies.Provider.RemoveLabelAsync(workItem, WorkflowLabels.WorkingState, cancellationToken).ConfigureAwait(false);
        }

        if (canonicalComment.Body.Contains(CorruptionWarningMarker, StringComparison.Ordinal)) return;
        var warning = $"""

            > **IssueAgent paused:** {explanation}
            > Local workflow data was preserved. Repair the managed state or acknowledge recovery with `agent:cmd:continue`.
            {CorruptionWarningMarker}
            """;
        await dependencies.Provider.UpdateIssueCommentAsync(
            config.Repository,
            issueNumber,
            canonicalComment.Id,
            canonicalComment.Body.TrimEnd() + Environment.NewLine + Environment.NewLine + warning,
            cancellationToken).ConfigureAwait(false);
        await dependencies.Notifier.NotifyAsync(
            new WorkflowNotification(
                WorkflowNotificationKind.HumanActionRequired,
                config.Repository.Id,
                issueNumber,
                "unknown",
                explanation),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistCanonicalStateAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderComment canonicalComment,
        CanonicalCommentContent content,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        var updated = content with
        {
            State = CanonicalStateSerializer.ToDocument(state, content.State.PullOrMergeRequest),
        };
        await dependencies.Provider.UpdateIssueCommentAsync(
            config.Repository,
            issueNumber,
            canonicalComment.Id,
            CanonicalCommentMarkdown.Render(updated),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionLabelsAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowPhase phase,
        CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber);
        await LabelCatalog.EnsureAllAsync(dependencies.Provider, config.Repository, cancellationToken).ConfigureAwait(false);
        var currentLabels = await dependencies.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        var (toAdd, toRemove) = LabelProtocol.ComputeTransition(
            currentLabels,
            phase,
            WorkflowOperationalState.Waiting,
            commandsToConsume: []);
        foreach (var label in toAdd)
        {
            await dependencies.Provider.EnsureLabelAsync(
                config.Repository,
                LabelCatalog.All.First(candidate => candidate.Name == label),
                cancellationToken).ConfigureAwait(false);
        }
        if (toAdd.Count > 0)
        {
            await dependencies.Provider.AddLabelsAsync(workItem, toAdd, cancellationToken).ConfigureAwait(false);
        }
        foreach (var label in toRemove)
        {
            await dependencies.Provider.RemoveLabelAsync(workItem, label, cancellationToken).ConfigureAwait(false);
        }
    }
}
