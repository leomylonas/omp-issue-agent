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
    string Explanation);

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

        var mergeRequest = await dependencies.Provider
            .FindMergeRequestAsync(config.Repository, state.Branch, state.TargetBranch, cancellationToken)
            .ConfigureAwait(false);
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
            await new CancellationWorkflow(dependencies)
                .CleanupLocalStateAsync(config, state, cancellationToken)
                .ConfigureAwait(false);
            return new WorkflowReconciliationResult(ReconciliationDisposition.Completed, state, content, canonicalComment, "Terminal workflow local state is clean.");
        }
        var worktreePath = Path.Combine(config.WorkflowsStoragePath, state.WorkflowId.ToString(), "worktree");
        var worktreeExists = Directory.Exists(worktreePath);
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
        var localHeadIsAncestorOfRemote = localHead is not null &&
            remoteHead is not null &&
            !string.Equals(localHead, remoteHead, StringComparison.Ordinal) &&
            await dependencies.Git.IsAncestorAsync(config.Repository.Id, localHead, remoteHead, cancellationToken).ConfigureAwait(false);
        var labels = await dependencies.Provider
            .GetLabelsAsync(new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber), cancellationToken)
            .ConfigureAwait(false);
        var labelSnapshot = LabelProtocol.Analyze(labels);
        var decision = ReconciliationDecider.Decide(new ReconciliationInput(
            labelSnapshot,
            content,
            worktreeExists,
            localHead,
            remoteHead,
            worktreeDirty,
            PlanInputHashPresent: content.State.PlanInputHash is { Length: > 0 },
            LocalHeadIsAncestorOfRemote: localHeadIsAncestorOfRemote));

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
            decision.Explanation);
    }

    public async Task<WorkflowReconciliationResult> PauseForHumanAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderComment canonicalComment,
        CanonicalCommentContent content,
        WorkflowState state,
        WaitingReason reason,
        string explanation,
        CancellationToken cancellationToken)
    {
        var alreadyRecorded = state.OperationalState == WorkflowOperationalState.Waiting && state.WaitingReason == reason;
        var waitingState = state with
        {
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = reason,
            UpdatedAt = alreadyRecorded ? state.UpdatedAt : dependencies.Clock.UtcNow,
        };

        if (!alreadyRecorded)
        {
            await PersistCanonicalStateAsync(config, issueNumber, canonicalComment, content, waitingState, cancellationToken)
                .ConfigureAwait(false);
            await TransitionLabelsAsync(config, issueNumber, waitingState.Phase, cancellationToken).ConfigureAwait(false);
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

    private async Task EscalateCorruptionAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderComment canonicalComment,
        string explanation,
        CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber);
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
