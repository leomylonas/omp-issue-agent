using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

public enum WorkflowMode
{
    PlanOnly,
    Full,
}

/// <summary>
/// Implementation approval and execution (specification §20 "Implementation approval", §21
/// "PR/MR review and repeated revision", §23 "Published branch history/conflicts").
/// </summary>
public sealed class ImplementationWorkflow(WorkflowDependencies deps)
{
    public async Task<WorkflowOutcome> RunAsync(
        WorkflowRepositoryConfig config,
        WorkflowMode mode,
        long issueNumber,
        WorkflowState currentState,
        IOmpClient omp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(omp);

        await TransitionLabelsAsync(config, issueNumber, currentState.Phase, currentState.OperationalState, [WorkflowCommand.Implement], cancellationToken)
            .ConfigureAwait(false);

        if (mode == WorkflowMode.PlanOnly)
        {
            var restoredState = currentState with { WaitingReason = WaitingReason.PlanApproval, UpdatedAt = deps.Clock.UtcNow };
            await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
            await NotifyAsync(config, issueNumber, restoredState, WorkflowNotificationKind.HumanActionRequired,
                "This deployment is configured plan-only; implementation was not started. Approve manually or reconfigure to full mode.", cancellationToken)
                .ConfigureAwait(false);
            return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, restoredState, "plan-only mode: implementation command rejected.");
        }

        var canonicalComment = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowContractException("Cannot implement: no canonical comment was found for this issue.");
        var existingContent = CanonicalCommentMarkdown.Parse(canonicalComment.Body);

        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        if (IsPlanStale(issue, existingContent))
        {
            var staleState = currentState with { Phase = WorkflowPhase.Planned, OperationalState = WorkflowOperationalState.Waiting, WaitingReason = WaitingReason.ReplanRequired, UpdatedAt = deps.Clock.UtcNow };
            await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
            await NotifyAsync(config, issueNumber, staleState, WorkflowNotificationKind.HumanActionRequired,
                "The issue title or description changed since the approved plan. Replan before implementing.", cancellationToken)
                .ConfigureAwait(false);
            return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, staleState, "Plan is stale; replan required before implementation.");
        }

        var workingState = currentState with { Phase = WorkflowPhase.Implementing, OperationalState = WorkflowOperationalState.Working, WaitingReason = null };
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Implementing, WorkflowOperationalState.Working, [], cancellationToken).ConfigureAwait(false);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        await deps.Git.ResetWorktreeAsync(worktreePath, currentState.BaseCommit, cancellationToken).ConfigureAwait(false);

        var currentPlan = new PlanContext(existingContent.State.PlanRevision, existingContent.PlanText, existingContent.DecisionsAndRationale);
        var attachmentsPath = AttachmentsPath(config, currentState.WorkflowId);
        var context = await deps.ContextBuilder
            .BuildAsync(config.Repository, issueNumber, workingState, currentPlan, mergeRequest: null, attachmentsPath, cancellationToken)
            .ConfigureAwait(false);

        var implementOutcome = await OmpRunCollector
            .RunToCompletionAsync(omp, new OmpRunRequest(currentState.OmpSessionId, worktreePath, ImplementationPromptBuilder.BuildImplementationPrompt(context), config.OmpAllowedEnvironment), cancellationToken)
            .ConfigureAwait(false);
        if (!implementOutcome.Succeeded)
        {
            return await FailAsync(config, issueNumber, workingState, implementOutcome.Error!.Message, cancellationToken).ConfigureAwait(false);
        }

        var result = ImplementationResult.Parse(implementOutcome.Completed!.ResultJson);
        if (result.IsMaterialDeviation)
        {
            return await PauseForMaterialDeviationAsync(config, issueNumber, workingState, result, cancellationToken).ConfigureAwait(false);
        }

        if (await deps.Git.HasUncommittedChangesAsync(worktreePath, cancellationToken).ConfigureAwait(false))
        {
            var correctiveOutcome = await OmpRunCollector
                .RunToCompletionAsync(omp, new OmpRunRequest(currentState.OmpSessionId, worktreePath, ImplementationPromptBuilder.BuildCorrectivePrompt(), config.OmpAllowedEnvironment), cancellationToken)
                .ConfigureAwait(false);
            if (correctiveOutcome.Succeeded)
            {
                result = ImplementationResult.Parse(correctiveOutcome.Completed!.ResultJson);
            }
        }

        await deps.Git.FetchAsync(config.Repository.Id, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        var latestTargetCommit = await deps.Git.ResolveBranchCommitAsync(config.Repository.Id, currentState.TargetBranch, cancellationToken).ConfigureAwait(false);
        if (latestTargetCommit != currentState.BaseCommit)
        {
            var rebased = await deps.Git.TryRebaseOntoAsync(worktreePath, latestTargetCommit, config.GitIdentity, cancellationToken).ConfigureAwait(false);
            if (!rebased)
            {
                var conflictOutcome = await OmpRunCollector
                    .RunToCompletionAsync(omp, new OmpRunRequest(currentState.OmpSessionId, worktreePath, ImplementationPromptBuilder.BuildConflictResolutionPrompt(), config.OmpAllowedEnvironment), cancellationToken)
                    .ConfigureAwait(false);
                if (!conflictOutcome.Succeeded)
                {
                    return await FailAsync(config, issueNumber, workingState, "Failed to resolve rebase conflicts before first publication.", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (deps.Git.WorktreeRequiresLfs(worktreePath))
        {
            await deps.Git.UploadLfsObjectsAsync(worktreePath, currentState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        }

        await deps.Git.PushAsync(worktreePath, currentState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);

        var mergeRequest = await FindOrCreateMergeRequestAsync(config, issueNumber, issue, currentState, cancellationToken).ConfigureAwait(false);

        var publishedState = workingState with
        {
            Phase = WorkflowPhase.Review,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ReviewRequested,
            UpdatedAt = deps.Clock.UtcNow,
        };

        var content = new CanonicalCommentContent(
            existingContent.PlanText,
            existingContent.DecisionsAndRationale,
            result.RenderMarkdown(),
            CanonicalStateSerializer.ToDocument(publishedState, $"{config.Repository.Id}#{mergeRequest.Number}"));

        await UpsertCanonicalCommentAsync(config, issueNumber, content, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Review, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await NotifyAsync(config, issueNumber, publishedState, WorkflowNotificationKind.ImplementationReady, "Draft PR/MR is ready for review.", cancellationToken)
            .ConfigureAwait(false);

        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, publishedState, "Implementation published; awaiting review.");
    }

    private async Task<ProviderMergeRequest> FindOrCreateMergeRequestAsync(
        WorkflowRepositoryConfig config, long issueNumber, ProviderIssue issue, WorkflowState state, CancellationToken cancellationToken)
    {
        var existing = await deps.Provider.FindMergeRequestAsync(config.Repository, state.Branch, state.TargetBranch, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        return await deps.Provider.CreateDraftMergeRequestAsync(
            new CreateMergeRequestRequest(config.Repository, state.Branch, state.TargetBranch, issue.Title, string.Empty, IsDraft: true, issueNumber),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPlanStale(ProviderIssue issue, CanonicalCommentContent existingContent) =>
        issue.UpdatedAt > existingContent.State.UpdatedAt && existingContent.State.ApprovedPlanRevision is not null;

    private async Task<WorkflowOutcome> PauseForMaterialDeviationAsync(
        WorkflowRepositoryConfig config, long issueNumber, WorkflowState workingState, ImplementationResult result, CancellationToken cancellationToken)
    {
        var pausedState = workingState with
        {
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.MaterialPlanDeviation,
            UpdatedAt = deps.Clock.UtcNow,
        };
        var message = result.MaterialDeviationExplanation ?? "OMP identified a material deviation from the approved plan and paused before proceeding.";

        await TransitionLabelsAsync(config, issueNumber, pausedState.Phase, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await NotifyAsync(config, issueNumber, pausedState, WorkflowNotificationKind.HumanActionRequired, message, cancellationToken).ConfigureAwait(false);

        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, pausedState, message);
    }

    private async Task<WorkflowOutcome> FailAsync(WorkflowRepositoryConfig config, long issueNumber, WorkflowState workingState, string message, CancellationToken cancellationToken)
    {
        var failedState = workingState with { Phase = WorkflowPhase.Failed, OperationalState = WorkflowOperationalState.Waiting, WaitingReason = WaitingReason.ManualIntervention, UpdatedAt = deps.Clock.UtcNow };
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Failed, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await NotifyAsync(config, issueNumber, failedState, WorkflowNotificationKind.ImplementationFailed, message, cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Failed, failedState, message);
    }

    private Task NotifyAsync(WorkflowRepositoryConfig config, long issueNumber, WorkflowState state, WorkflowNotificationKind kind, string message, CancellationToken cancellationToken) =>
        deps.Notifier.NotifyAsync(new WorkflowNotification(kind, config.Repository.Id, issueNumber, state.WorkflowId.ToString(), message), cancellationToken);

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
