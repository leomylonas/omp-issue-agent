using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

/// <summary>
/// Initial planning and replanning (specification §20 "Initial planning" and "Replanning", §18
/// iterative planning). Both publish/update the single canonical comment in place and never create
/// a second plan comment.
/// </summary>
public sealed class PlanningWorkflow(WorkflowDependencies deps)
{
    /// <summary>Runs initial planning for a newly eligible assignment end to end: creates the
    /// workflow id, OMP session, and canonical comment; prepares the retained worktree; plans; and
    /// publishes the plan with the workflow set to planned/waiting.</summary>
    public async Task<WorkflowOutcome> RunInitialPlanningAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        IOmpClient omp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(omp);

        var workflowId = WorkflowId.New();
        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var targetBranch = config.TargetBranchOverride;
        var baseCommit = await deps.Git.ResolveBranchCommitAsync(config.Repository.Id, targetBranch, cancellationToken).ConfigureAwait(false);

        var session = await omp.CreateSessionAsync(config.PlanningRole, cancellationToken).ConfigureAwait(false);
        var branchName = BranchNaming.DeriveBranchName(issueNumber, issue.Title);

        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planning, WorkflowOperationalState.Working, [], cancellationToken)
            .ConfigureAwait(false);

        var worktreePath = WorktreePath(config, workflowId);
        var attachmentsPath = AttachmentsPath(config, workflowId);
        await deps.Git.CreateWorktreeAsync(config.Repository.Id, workflowId.ToString(), worktreePath, branchName, baseCommit, cancellationToken)
            .ConfigureAwait(false);
        await PrepareWorktreeContentAsync(config, worktreePath, cancellationToken).ConfigureAwait(false);

        var initialState = new WorkflowState(
            workflowId, WorkflowPhase.Planning, WorkflowOperationalState.Working, null,
            PlanRevision: 0, ApprovedPlanRevision: null, session.SessionId, branchName, targetBranch, baseCommit, deps.Clock.UtcNow);

        var context = await deps.ContextBuilder
            .BuildAsync(config.Repository, issueNumber, initialState, currentPlan: null, mergeRequest: null, attachmentsPath, cancellationToken)
            .ConfigureAwait(false);

        var prompt = config.ApplyInstructions(PlanningPromptBuilder.BuildInitialPlanPrompt(context));
        var outcome = await OmpRunCollector
            .RunToCompletionAsync(omp, new OmpRunRequest(session.SessionId, worktreePath, prompt, config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return await FailAsync(config, issueNumber, initialState, WaitingReason.ManualIntervention, outcome.Error!.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        var planningResult = PlanningResult.Parse(outcome.Completed!.ResultJson);
        var reconciledResult = await ReconcileNewInputDuringPlanningAsync(
            config, issueNumber, omp, session.SessionId, worktreePath, context, planningResult, cancellationToken)
            .ConfigureAwait(false);

        return await PublishPlanAsync(config, issueNumber, initialState, reconciledResult, planRevision: 1, issue.Title, issue.Description, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes the same OMP session with the complete relevant planning conversation and
    /// revises the existing plan (specification §18, §20 "Replanning").</summary>
    public async Task<WorkflowOutcome> RunReplanAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        IOmpClient omp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(omp);

        var workingState = currentState with
        {
            Phase = WorkflowPhase.Planning,
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
        };
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planning, WorkflowOperationalState.Working, [WorkflowCommand.Replan], cancellationToken)
            .ConfigureAwait(false);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        var attachmentsPath = AttachmentsPath(config, currentState.WorkflowId);

        var canonicalComment = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        if (canonicalComment is null)
        {
            return await FailAsync(
                config, issueNumber, workingState, WaitingReason.CorruptState,
                "Cannot replan: no canonical comment was found for this issue.", cancellationToken).ConfigureAwait(false);
        }
        var existingContent = CanonicalCommentMarkdown.Parse(canonicalComment.Body);
        var currentPlan = new PlanContext(existingContent.State.PlanRevision, existingContent.PlanText, existingContent.DecisionsAndRationale);

        var context = await deps.ContextBuilder
            .BuildAsync(config.Repository, issueNumber, workingState, currentPlan, mergeRequest: null, attachmentsPath, cancellationToken)
            .ConfigureAwait(false);

        var feedback = context.PrimaryIssue.HumanComments
            .Where(c => c.CreatedAt > existingContent.State.UpdatedAt)
            .ToList();

        var prompt = config.ApplyInstructions(PlanningPromptBuilder.BuildReplanPrompt(context, feedback));
        var outcome = await OmpRunCollector
            .RunToCompletionAsync(omp, new OmpRunRequest(currentState.OmpSessionId, worktreePath, prompt, config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return await FailAsync(config, issueNumber, workingState, WaitingReason.ManualIntervention, outcome.Error!.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        var planningResult = PlanningResult.Parse(outcome.Completed!.ResultJson);
        return await PublishPlanAsync(config, issueNumber, workingState, planningResult, existingContent.State.PlanRevision + 1, context.PrimaryIssue.Title, context.PrimaryIssue.Description, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PlanningResult> ReconcileNewInputDuringPlanningAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        IOmpClient omp,
        string sessionId,
        string worktreePath,
        AgentContext contextUsedForPlanning,
        PlanningResult initialResult,
        CancellationToken cancellationToken)
    {
        var latestComments = new List<HumanComment>();
        await foreach (var comment in deps.Provider.GetIssueCommentsAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            if (!comment.IsBot && !CanonicalCommentMarkdown.IsCanonicalComment(comment.Body))
            {
                latestComments.Add(new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body));
            }
        }

        var newComments = latestComments
            .Where(latest => !contextUsedForPlanning.PrimaryIssue.HumanComments.Any(seen => seen.CreatedAt == latest.CreatedAt && seen.Author == latest.Author))
            .ToList();

        if (newComments.Count == 0)
        {
            return initialResult;
        }

        var prompt = config.ApplyInstructions(PlanningPromptBuilder.BuildReplanPrompt(
            contextUsedForPlanning with { CurrentPlan = new PlanContext(0, initialResult.PlanText, initialResult.DecisionsAndRationale) },
            newComments));

        var outcome = await OmpRunCollector
            .RunToCompletionAsync(omp, new OmpRunRequest(sessionId, worktreePath, prompt, config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
            .ConfigureAwait(false);

        return outcome.Succeeded ? PlanningResult.Parse(outcome.Completed!.ResultJson) : initialResult;
    }

    private async Task<WorkflowOutcome> PublishPlanAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        PlanningResult planningResult,
        int planRevision,
        string issueTitle,
        string issueDescription,
        CancellationToken cancellationToken)
    {
        var publishedState = workingState with
        {
            Phase = WorkflowPhase.Planned,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.PlanApproval,
            PlanRevision = planRevision,
            UpdatedAt = deps.Clock.UtcNow,
            PlanInputHash = PlanInputHasher.Compute(issueTitle, issueDescription),
        };

        var content = new CanonicalCommentContent(
            planningResult.PlanText,
            planningResult.DecisionsAndRationale,
            ImplementationResult: null,
            CanonicalStateSerializer.ToDocument(publishedState, pullOrMergeRequest: null));

        await UpsertCanonicalCommentAsync(config, issueNumber, content, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, [], cancellationToken)
            .ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.PlanReady, config.Repository.Id, issueNumber, publishedState.WorkflowId.ToString(), "Plan is ready for review."),
            cancellationToken).ConfigureAwait(false);

        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, publishedState, "Plan published; awaiting human approval.");
    }

    private async Task<WorkflowOutcome> FailAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        WaitingReason reason,
        string message,
        CancellationToken cancellationToken)
    {
        var failedState = workingState with
        {
            Phase = WorkflowPhase.Failed,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = reason,
            UpdatedAt = deps.Clock.UtcNow,
        };

        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Failed, WorkflowOperationalState.Waiting, [], cancellationToken)
            .ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.PlanFailed, config.Repository.Id, issueNumber, failedState.WorkflowId.ToString(), message),
            cancellationToken).ConfigureAwait(false);

        return new WorkflowOutcome(WorkflowOutcomeStatus.Failed, failedState, message);
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
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowPhase phase,
        WorkflowOperationalState operationalState,
        IReadOnlyCollection<WorkflowCommand> commandsToConsume,
        CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(config.Repository, ProviderWorkItemKind.Issue, issueNumber);
        var currentLabels = await deps.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        var (toAdd, toRemove) = LabelProtocol.ComputeTransition(currentLabels, phase, operationalState, commandsToConsume);

        foreach (var label in toAdd)
        {
            var catalogEntry = LabelCatalog.All.First(l => l.Name == label);
            await deps.Provider.EnsureLabelAsync(config.Repository, catalogEntry, cancellationToken).ConfigureAwait(false);
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

    private async Task PrepareWorktreeContentAsync(WorkflowRepositoryConfig config, string worktreePath, CancellationToken cancellationToken)
    {
        var submoduleAuthenticationResolver = config.SubmoduleAuthenticationResolver ?? (_ => null);
        await deps.Git.UpdateSubmodulesAsync(config.Repository.Id, worktreePath, submoduleAuthenticationResolver, cancellationToken).ConfigureAwait(false);
        if (deps.Git.WorktreeRequiresLfs(worktreePath))
        {
            await deps.Git.MaterializeLfsContentAsync(config.Repository.Id, worktreePath, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string WorktreePath(WorkflowRepositoryConfig config, WorkflowId workflowId) =>
        Path.Combine(config.WorkflowsStoragePath, workflowId.ToString(), "worktree");

    private static string AttachmentsPath(WorkflowRepositoryConfig config, WorkflowId workflowId) =>
        Path.Combine(config.WorkflowsStoragePath, workflowId.ToString(), "attachments");
}
