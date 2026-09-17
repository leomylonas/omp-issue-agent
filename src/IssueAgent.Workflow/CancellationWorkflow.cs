using IssueAgent.Domain;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

/// <summary>Explicit cancellation (specification §24) and completion-detected cleanup for a merged
/// or closed-without-merge PR/MR. IssueAgent never deletes remote branches and never changes
/// assignees.</summary>
public sealed class CancellationWorkflow(WorkflowDependencies deps)
{
    /// <summary><c>agent:cmd:cancel</c> is valid during active work: request OMP cancellation,
    /// transition to cancelled, and clean up local state.</summary>
    public async Task<WorkflowOutcome> RunAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        IOmpClient? omp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (omp is not null)
        {
            await omp.CancelAsync(currentState.OmpSessionId, cancellationToken).ConfigureAwait(false);
        }

        var cancelledState = currentState with
        {
            Phase = WorkflowPhase.Cancelled,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ManualIntervention,
            UpdatedAt = deps.Clock.UtcNow,
        };

        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Cancelled, WorkflowOperationalState.Waiting, [WorkflowCommand.Cancel], cancellationToken)
            .ConfigureAwait(false);

        await CleanUpLocalStateAsync(config, currentState, cancellationToken).ConfigureAwait(false);

        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.Cancelled, config.Repository.Id, issueNumber, cancelledState.WorkflowId.ToString(), "Workflow was cancelled by human command."),
            cancellationToken).ConfigureAwait(false);

        return new WorkflowOutcome(WorkflowOutcomeStatus.Progressed, cancelledState, "Cancelled.");
    }

    /// <summary>Called once a poller observes the PR/MR merged: removes the local worktree and
    /// local agent branch, leaves the remote branch alone, and sets phase done. Issue closure
    /// itself follows provider-native closing syntax already included in the PR/MR body.</summary>
    public async Task<WorkflowOutcome> CompleteOnMergeAsync(
        WorkflowRepositoryConfig config, long issueNumber, WorkflowState currentState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var doneState = currentState with { Phase = WorkflowPhase.Done, OperationalState = WorkflowOperationalState.Waiting, WaitingReason = WaitingReason.ManualIntervention, UpdatedAt = deps.Clock.UtcNow };
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Done, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await CleanUpLocalStateAsync(config, currentState, cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Progressed, doneState, "Merged; workflow complete.");
    }

    /// <summary>Called once a poller observes the PR/MR closed without merging.</summary>
    public async Task<WorkflowOutcome> CompleteOnCloseWithoutMergeAsync(
        WorkflowRepositoryConfig config, long issueNumber, WorkflowState currentState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var cancelledState = currentState with { Phase = WorkflowPhase.Cancelled, OperationalState = WorkflowOperationalState.Waiting, WaitingReason = WaitingReason.ManualIntervention, UpdatedAt = deps.Clock.UtcNow };
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Cancelled, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await CleanUpLocalStateAsync(config, currentState, cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Progressed, cancelledState, "Closed without merge; workflow cancelled.");
    }

    private async Task CleanUpLocalStateAsync(WorkflowRepositoryConfig config, WorkflowState state, CancellationToken cancellationToken)
    {
        var worktreePath = Path.Combine(config.WorkflowsStoragePath, state.WorkflowId.ToString(), "worktree");
        await deps.Git.RemoveWorktreeAsync(config.Repository.Id, state.WorkflowId.ToString(), worktreePath, cancellationToken).ConfigureAwait(false);
        await deps.Git.RemoveLocalBranchAsync(config.Repository.Id, state.Branch, cancellationToken).ConfigureAwait(false);
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
}
