using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

/// <summary>PR/MR review and repeated revision (specification §21). An unlimited conversation loop:
/// humans leave review feedback, add <c>agent:cmd:revise</c>, OMP revises on the same session, and
/// IssueAgent pushes without ever force-pushing.</summary>
public sealed class RevisionWorkflow(WorkflowDependencies deps)
{
    public async Task<WorkflowOutcome> RunAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        IOmpClient omp,
        CancellationToken cancellationToken,
        bool publishRetainedResult = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(omp);

        var workingState = currentState with
        {
            Phase = WorkflowPhase.Revising,
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
            InterruptedPhase = null,
            UpdatedAt = deps.Clock.UtcNow,
        };

        var canonicalComment = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        if (canonicalComment is null)
        {
            return await EscalateWithoutCanonicalCommentAsync(
                config, issueNumber, workingState, "Cannot revise: no canonical comment was found for this issue.", cancellationToken).ConfigureAwait(false);
        }
        var existingContent = CanonicalCommentMarkdown.Parse(canonicalComment.Body);
        if (existingContent.State.ReviewFeedbackCutoff is null)
        {
            return await EscalateAsync(
                config,
                issueNumber,
                currentState,
                existingContent,
                "Cannot revise: the review-feedback checkpoint is missing, so IssueAgent cannot determine which feedback this revision must address.",
                cancellationToken).ConfigureAwait(false);
        }
        var retainedResult = publishRetainedResult && currentState.Phase == WorkflowPhase.Revising
            ? existingContent.ImplementationResult
            : null;
        var workingContent = existingContent with
        {
            // A fresh revise command must not let the prior implementation result masquerade as a
            // completed revision; only an explicit continuation may publish its checkpoint.
            ImplementationResult = retainedResult,
            State = CanonicalStateSerializer.ToDocument(workingState, existingContent.State.PullOrMergeRequest),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, workingContent, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Revising, WorkflowOperationalState.Working, [WorkflowCommand.Revise], cancellationToken)
            .ConfigureAwait(false);
        var mergeRequest = await deps.Provider.FindMergeRequestAsync(config.Repository, currentState.Branch, currentState.TargetBranch, cancellationToken).ConfigureAwait(false);
        if (mergeRequest is null)
        {
            return await EscalateAsync(
                config, issueNumber, workingState, existingContent, "Cannot revise: no merge request was found for this workflow's branch.", cancellationToken).ConfigureAwait(false);
        }
        await ConsumeMergeRequestCommandAsync(config, mergeRequest.Number, WorkflowCommand.Revise, cancellationToken).ConfigureAwait(false);

        if (retainedResult is { Length: > 0 })
        {
            var feedbackBeforePublication = await CaptureFeedbackSnapshotAsync(config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false);
            return await PublishRevisionAsync(
                config, issueNumber, workingState, workingContent, retainedResult, mergeRequest, feedbackBeforePublication, omp, cancellationToken)
                .ConfigureAwait(false);
        }
        var feedbackBeforeRevision = await CaptureFeedbackSnapshotAsync(config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false);
        var currentPlan = new PlanContext(existingContent.State.PlanRevision, existingContent.PlanText, existingContent.DecisionsAndRationale);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        var attachmentsPath = AttachmentsPath(config, currentState.WorkflowId);
        var context = await deps.ContextBuilder
            .BuildAsync(config.Repository, issueNumber, workingState, currentPlan, mergeRequest, attachmentsPath, cancellationToken)
            .ConfigureAwait(false);

        var reviewFeedbackCutoff = existingContent.State.ReviewFeedbackCutoff.Value;
        var feedback = context.PullOrMergeRequest is { } mrContext
            ? mrContext.Comments.Concat(mrContext.ReviewThreads).Where(c => c.CreatedAt > reviewFeedbackCutoff).ToList()
            : [];
        await omp.SelectRoleAsync(config.RevisionRole, cancellationToken).ConfigureAwait(false);
        var revisionOutcome = await OmpRunCollector
            .RunToCompletionAsync(omp, new OmpRunRequest(currentState.OmpSessionId, worktreePath, config.ApplyInstructions(ImplementationPromptBuilder.BuildRevisionPrompt(context, feedback)), config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
            .ConfigureAwait(false);
        if (!revisionOutcome.Succeeded)
        {
            return await FailAsync(config, issueNumber, workingState, revisionOutcome.Error!.Message, cancellationToken).ConfigureAwait(false);
        }

        ImplementationResult result;
        try
        {
            result = ImplementationResult.Parse(revisionOutcome.Completed!.ResultJson);
        }
        catch (WorkflowContractException exception)
        {
            return await FailAsync(config, issueNumber, workingState, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        if (result.IsMaterialDeviation)
        {
            return await PauseForMaterialDeviationAsync(
                config, issueNumber, workingState, workingContent, result, cancellationToken).ConfigureAwait(false);
        }

        var resultMarkdown = result.RenderMarkdown();
        return await PublishRevisionAsync(
            config, issueNumber, workingState, workingContent, resultMarkdown, mergeRequest, feedbackBeforeRevision, omp, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowOutcome> PublishRevisionAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent workingContent,
        string resultMarkdown,
        ProviderMergeRequest mergeRequest,
        HashSet<string> feedbackBeforeRevision,
        IOmpClient omp,
        CancellationToken cancellationToken)
    {
        // The checkpoint includes both the result and the revising/working state. A restart between
        // recording the result and pushing can therefore only resume this retained revision.
        var publicationCheckpoint = workingContent with { ImplementationResult = resultMarkdown };
        await UpsertCanonicalCommentAsync(config, issueNumber, publicationCheckpoint, cancellationToken).ConfigureAwait(false);

        await deps.Git.FetchAsync(config.Repository.Id, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        var latestTargetCommit = await deps.Git
            .ResolveBranchCommitAsync(config.Repository.Id, workingState.TargetBranch, cancellationToken)
            .ConfigureAwait(false);
        var merged = await deps.Git
            .TryMergeAsync(config.Repository.Id, WorktreePath(config, workingState.WorkflowId), latestTargetCommit, config.GitIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (!merged)
        {
            await omp.SelectRoleAsync(config.ConflictResolutionRole, cancellationToken).ConfigureAwait(false);
            var conflictOutcome = await OmpRunCollector
                .RunToCompletionAsync(
                    omp,
                    new OmpRunRequest(
                        workingState.OmpSessionId,
                        WorktreePath(config, workingState.WorkflowId),
                        config.ApplyInstructions(ImplementationPromptBuilder.BuildConflictResolutionPrompt()),
                        config.OmpAllowedEnvironment,
                        config.OmpTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!conflictOutcome.Succeeded)
            {
                return await FailAsync(
                    config,
                    issueNumber,
                    workingState,
                    "Failed to resolve conflicts while merging the latest target branch.",
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                _ = ImplementationResult.Parse(conflictOutcome.Completed!.ResultJson);
            }
            catch (WorkflowContractException exception)
            {
                return await FailAsync(config, issueNumber, workingState, exception.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        var feedbackAfterRevision = await CaptureFeedbackSnapshotAsync(config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false);
        if (!feedbackAfterRevision.SetEquals(feedbackBeforeRevision))
        {
            return await PauseForNewFeedbackAsync(
                config,
                issueNumber,
                workingState,
                publicationCheckpoint,
                resultMarkdown,
                "New review feedback arrived while OMP was revising. The retained worktree was preserved; review the feedback and request another revision.",
                cancellationToken).ConfigureAwait(false);
        }

        var worktreePath = WorktreePath(config, workingState.WorkflowId);
        if (deps.Git.WorktreeRequiresLfs(worktreePath))
        {
            await deps.Git.UploadLfsObjectsAsync(config.Repository.Id, worktreePath, workingState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        }
        await deps.Git.PushAsync(config.Repository.Id, worktreePath, workingState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);

        var publishedAt = deps.Clock.UtcNow;
        var publishedState = workingState with
        {
            Phase = WorkflowPhase.Review,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ReviewRequested,
            InterruptedPhase = null,
            UpdatedAt = publishedAt,
            ReviewFeedbackCutoff = publishedAt,
        };

        var content = new CanonicalCommentContent(
            workingContent.PlanText,
            workingContent.DecisionsAndRationale,
            resultMarkdown,
            CanonicalStateSerializer.ToDocument(publishedState, $"{config.Repository.Id}#{mergeRequest.Number}"));

        await UpsertCanonicalCommentAsync(config, issueNumber, content, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Review, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.ImplementationReady, config.Repository.Id, issueNumber, publishedState.WorkflowId.ToString(), "Revision published; awaiting review."),
            cancellationToken).ConfigureAwait(false);

        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, publishedState, "Revision published; awaiting review.");
    }

    private async Task ConsumeMergeRequestCommandAsync(
        WorkflowRepositoryConfig config,
        long mergeRequestNumber,
        WorkflowCommand command,
        CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.MergeRequest, mergeRequestNumber);
        var commandLabel = command switch
        {
            WorkflowCommand.Revise => WorkflowCommandLabels.Revise,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var labels = await deps.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        if (labels.Contains(commandLabel))
        {
            await deps.Provider.RemoveLabelAsync(workItem, commandLabel, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HashSet<string>> CaptureFeedbackSnapshotAsync(
        RepositoryRef repository,
        long mergeRequestNumber,
        CancellationToken cancellationToken)
    {
        var snapshot = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var comment in deps.Provider.GetMergeRequestCommentsAsync(repository, mergeRequestNumber, cancellationToken).ConfigureAwait(false))
        {
            if (!comment.IsBot && !CanonicalCommentMarkdown.IsCanonicalComment(comment.Body))
            {
                snapshot.Add($"comment:{comment.Id}:{comment.UpdatedAt:O}:{comment.Body}");
            }
        }

        await foreach (var thread in deps.Provider.GetReviewThreadsAsync(repository, mergeRequestNumber, cancellationToken).ConfigureAwait(false))
        {
            snapshot.Add($"thread:{thread.Id}:resolved={thread.IsResolved}");
            foreach (var comment in thread.Comments)
            {
                if (!comment.IsBot && !CanonicalCommentMarkdown.IsCanonicalComment(comment.Body))
                {
                    snapshot.Add($"thread:{thread.Id}:{comment.Id}:{comment.UpdatedAt:O}:{comment.Body}");
                }
            }
        }

        return snapshot;
    }

    private Task<WorkflowOutcome> PauseForMaterialDeviationAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent existingContent,
        ImplementationResult result,
        CancellationToken cancellationToken) =>
        PauseForNewFeedbackAsync(
            config,
            issueNumber,
            workingState,
            existingContent,
            result.RenderMarkdown(),
            result.MaterialDeviationExplanation!,
            cancellationToken,
            WaitingReason.MaterialPlanDeviation);

    private async Task<WorkflowOutcome> PauseForNewFeedbackAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent existingContent,
        string implementationResult,
        string message,
        CancellationToken cancellationToken,
        WaitingReason reason = WaitingReason.NewFeedbackDuringRevision)
    {
        var pausedState = workingState with
        {
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = reason,
            UpdatedAt = deps.Clock.UtcNow,
        };
        await UpsertCanonicalCommentAsync(
            config,
            issueNumber,
            existingContent with
            {
                ImplementationResult = implementationResult,
                State = CanonicalStateSerializer.ToDocument(pausedState, existingContent.State.PullOrMergeRequest),
            },
            cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, pausedState.Phase, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.HumanActionRequired, config.Repository.Id, issueNumber, pausedState.WorkflowId.ToString(), message),
            cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, pausedState, message);
    }

    private async Task<WorkflowOutcome> FailAsync(WorkflowRepositoryConfig config, long issueNumber, WorkflowState workingState, string message, CancellationToken cancellationToken)
    {
        var failedState = workingState with
        {
            Phase = WorkflowPhase.Failed,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            InterruptedPhase = workingState.Phase,
            UpdatedAt = deps.Clock.UtcNow,
        };
        var canonical = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        if (canonical is not null)
        {
            try
            {
                var content = CanonicalCommentMarkdown.Parse(canonical.Body);
                await UpsertCanonicalCommentAsync(
                    config,
                    issueNumber,
                    content with { State = CanonicalStateSerializer.ToDocument(failedState, content.State.PullOrMergeRequest) },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CanonicalCommentCorruptException)
            {
            }
        }
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Failed, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.RevisionFailed, config.Repository.Id, issueNumber, failedState.WorkflowId.ToString(), message),
            cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Failed, failedState, message);
    }

    /// <summary>Preserves state and escalates to a human instead of throwing an unhandled exception
    /// when the workflow cannot safely progress (specification §25): the canonical comment exists
    /// but a required linked resource (here, the merge request) does not.</summary>
    private async Task<WorkflowOutcome> EscalateAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent existingContent,
        string message,
        CancellationToken cancellationToken)
    {
        var pausedState = workingState with
        {
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.CorruptState,
            UpdatedAt = deps.Clock.UtcNow,
        };
        await UpsertCanonicalCommentAsync(
            config,
            issueNumber,
            existingContent with { State = CanonicalStateSerializer.ToDocument(pausedState, existingContent.State.PullOrMergeRequest) },
            cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, pausedState.Phase, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.HumanActionRequired, config.Repository.Id, issueNumber, pausedState.WorkflowId.ToString(), message),
            cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, pausedState, message);
    }

    /// <summary>Same as <see cref="EscalateAsync"/> but for the case where no canonical comment
    /// exists to update; still transitions labels and notifies rather than throwing.</summary>
    private async Task<WorkflowOutcome> EscalateWithoutCanonicalCommentAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        string message,
        CancellationToken cancellationToken)
    {
        var pausedState = workingState with
        {
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.CorruptState,
            UpdatedAt = deps.Clock.UtcNow,
        };
        await TransitionLabelsAsync(config, issueNumber, pausedState.Phase, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.HumanActionRequired, config.Repository.Id, issueNumber, pausedState.WorkflowId.ToString(), message),
            cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, pausedState, message);
    }

    private async Task UpsertCanonicalCommentAsync(WorkflowRepositoryConfig config, long issueNumber, CanonicalCommentContent content, CancellationToken cancellationToken)
    {
        var body = CanonicalCommentMarkdown.Render(content);
        var existing = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await deps.Provider.CreateIssueCommentAsync(config.Repository, issueNumber, body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await deps.Provider.UpdateIssueCommentAsync(config.Repository, issueNumber, existing.Id, body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TransitionLabelsAsync(
        WorkflowRepositoryConfig config, long issueNumber, WorkflowPhase phase, WorkflowOperationalState operationalState,
        IReadOnlyCollection<WorkflowCommand> commandsToConsume, CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber);
        await LabelCatalog.EnsureAllAsync(deps.Provider, config.Repository, cancellationToken).ConfigureAwait(false);
        var currentLabels = await deps.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        var (toAdd, toRemove) = LabelProtocol.ComputeTransition(currentLabels, phase, operationalState, commandsToConsume);

        foreach (var label in toAdd)
        {
            await deps.Provider.EnsureLabelAsync(config.Repository, LabelCatalog.All.First(l => l.Name == label), cancellationToken).ConfigureAwait(false);
        }

        if (toAdd.Count > 0)
        {
            await deps.Provider.AddLabelsAsync(workItem, toAdd, cancellationToken).ConfigureAwait(false);
        }

        foreach (var label in toRemove)
        {
            await deps.Provider.RemoveLabelAsync(workItem, label, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string WorktreePath(WorkflowRepositoryConfig config, WorkflowId workflowId) =>
        Path.Combine(config.WorkflowsStoragePath, workflowId.ToString(), "worktree");

    private static string AttachmentsPath(WorkflowRepositoryConfig config, WorkflowId workflowId) =>
        Path.Combine(config.WorkflowsStoragePath, workflowId.ToString(), "attachments");
}
