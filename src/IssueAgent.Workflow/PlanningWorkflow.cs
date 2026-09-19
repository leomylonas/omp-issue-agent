using System.Security.Cryptography;
using System.Text;
using IssueAgent.Context;
using System.Diagnostics;
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
        CancellationToken cancellationToken,
        WorkflowId? suppliedWorkflowId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(omp);
        var workflowId = suppliedWorkflowId ?? WorkflowId.New();
        Activity.Current?.SetTag("WorkflowId", workflowId.ToString());
        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var targetBranch = config.TargetBranchOverride;
        var baseCommit = await deps.Git.ResolveBranchCommitAsync(config.Repository.Id, targetBranch, cancellationToken).ConfigureAwait(false);

        var session = await omp.CreateSessionAsync(config.PlanningRole, cancellationToken).ConfigureAwait(false);
        var branchName = BranchNaming.DeriveBranchName(issueNumber, issue.Title);
        var initialState = new WorkflowState(
            workflowId, WorkflowPhase.Planning, WorkflowOperationalState.Working, null,
            PlanRevision: 0, ApprovedPlanRevision: null, session.SessionId, branchName, targetBranch, baseCommit,
            deps.Clock.UtcNow, PlanInputHasher.Compute(issue.Title, issue.Description), session.SessionFile);

        // Publish the planning/working checkpoint before creating any retained local state or
        // invoking OMP. A restart can therefore distinguish an interrupted first attempt from a
        // never-started workflow and preserve the stable workflow/session identity.
        await UpsertCanonicalCommentAsync(
            config,
            issueNumber,
            new CanonicalCommentContent(
                "Planning is in progress.",
                [],
                null,
                CanonicalStateSerializer.ToDocument(initialState, pullOrMergeRequest: null)),
            cancellationToken).ConfigureAwait(false);

        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planning, WorkflowOperationalState.Working, [], cancellationToken)
            .ConfigureAwait(false);

        var worktreePath = WorktreePath(config, workflowId);
        var attachmentsPath = AttachmentsPath(config, workflowId);
        await deps.Git.CreateWorktreeAsync(config.Repository.Id, workflowId.ToString(), worktreePath, branchName, baseCommit, cancellationToken)
            .ConfigureAwait(false);
        await PrepareWorktreeContentAsync(config, worktreePath, cancellationToken).ConfigureAwait(false);
        var planningInput = await CaptureInputSnapshotAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
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

        try
        {
            var planningResult = PlanningResult.Parse(outcome.Completed!.ResultJson);
            var reconciledResult = await ReconcileNewInputDuringPlanningAsync(
                config, issueNumber, omp, session.SessionId, worktreePath, context, planningResult, planningInput, currentPlan: null, cancellationToken)
                .ConfigureAwait(false);
            return await PublishPlanAsync(
                config,
                issueNumber,
                initialState,
                reconciledResult.Result,
                planRevision: 1,
                reconciledResult.Input.Title,
                reconciledResult.Input.Description,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowContractException exception)
        {
            return await FailAsync(config, issueNumber, initialState, WaitingReason.ManualIntervention, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
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
            InterruptedPhase = null,
            UpdatedAt = deps.Clock.UtcNow,
        };

        var canonicalComment = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        if (canonicalComment is null)
        {
            return await FailAsync(
                config, issueNumber, workingState, WaitingReason.CorruptState,
                "Cannot replan: no canonical comment was found for this issue.", cancellationToken).ConfigureAwait(false);
        }
        var existingContent = CanonicalCommentMarkdown.Parse(canonicalComment.Body);
        await UpsertCanonicalCommentAsync(
            config,
            issueNumber,
            existingContent with
            {
                State = CanonicalStateSerializer.ToDocument(workingState, existingContent.State.PullOrMergeRequest),
            },
            cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planning, WorkflowOperationalState.Working, [WorkflowCommand.Replan], cancellationToken)
            .ConfigureAwait(false);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        var attachmentsPath = AttachmentsPath(config, currentState.WorkflowId);
        await PrepareWorktreeContentAsync(config, worktreePath, cancellationToken).ConfigureAwait(false);
        var currentPlan = new PlanContext(existingContent.State.PlanRevision, existingContent.PlanText, existingContent.DecisionsAndRationale);

        var planningInput = await CaptureInputSnapshotAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
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
            return await FailAsync(
                config,
                issueNumber,
                workingState,
                WaitingReason.ManualIntervention,
                outcome.Error?.Message ?? "OMP replanning failed.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var planningResult = PlanningResult.Parse(outcome.Completed!.ResultJson);
            var reconciled = await ReconcileNewInputDuringPlanningAsync(
                config, issueNumber, omp, currentState.OmpSessionId, worktreePath, context, planningResult, planningInput, currentPlan, cancellationToken)
                .ConfigureAwait(false);
            return await PublishPlanAsync(
                config,
                issueNumber,
                workingState,
                reconciled.Result,
                existingContent.State.PlanRevision + 1,
                reconciled.Input.Title,
                reconciled.Input.Description,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowContractException exception)
        {
            return await FailAsync(config, issueNumber, workingState, WaitingReason.ManualIntervention, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed record InputSnapshot(string Title, string Description, string CommentsDigest);

    private sealed record ReconciledPlanningResult(PlanningResult Result, InputSnapshot Input);
    private async Task<InputSnapshot> CaptureInputSnapshotAsync(
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var issue = await deps.Provider.GetIssueAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var commentStamps = new List<string>();
        await foreach (var comment in deps.Provider.GetIssueCommentsAsync(repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            if (!comment.IsBot && !CanonicalCommentMarkdown.IsCanonicalComment(comment.Body))
            {
                commentStamps.Add($"{comment.Id}:{comment.UpdatedAt:O}:{comment.Body}");
            }
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', commentStamps))));
        return new InputSnapshot(issue.Title, issue.Description, digest);
    }

    private async Task<ReconciledPlanningResult> ReconcileNewInputDuringPlanningAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        IOmpClient omp,
        string sessionId,
        string worktreePath,
        AgentContext contextUsedForPlanning,
        PlanningResult initialResult,
        InputSnapshot inputSnapshot,
        PlanContext? currentPlan,
        CancellationToken cancellationToken)
    {
        var result = initialResult;
        var context = contextUsedForPlanning;
        var baseline = inputSnapshot;
        while (true)
        {
            var latest = await CaptureInputSnapshotAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
            if (latest == baseline)
            {
                return new ReconciledPlanningResult(result, baseline);
            }

            var latestPlan = new PlanContext(currentPlan?.Revision ?? 0, result.PlanText, result.DecisionsAndRationale);
            context = await deps.ContextBuilder
                .BuildAsync(config.Repository, issueNumber, context.WorkflowState, latestPlan, mergeRequest: null,
                    AttachmentsPath(config, context.WorkflowState.WorkflowId), cancellationToken)
                .ConfigureAwait(false);
            var feedback = context.PrimaryIssue.HumanComments
                .Where(comment => !contextUsedForPlanning.PrimaryIssue.HumanComments.Any(
                    previous => previous.Author == comment.Author && previous.CreatedAt == comment.CreatedAt && previous.Body == comment.Body))
                .ToList();
            var prompt = config.ApplyInstructions(PlanningPromptBuilder.BuildReplanPrompt(
                context with { CurrentPlan = latestPlan }, feedback));
            var outcome = await OmpRunCollector
                .RunToCompletionAsync(omp, new OmpRunRequest(sessionId, worktreePath, prompt, config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                throw new WorkflowContractException($"OMP reconciliation run failed: {outcome.Error?.Message ?? "unknown error"}");
            }

            result = PlanningResult.Parse(outcome.Completed!.ResultJson);
            contextUsedForPlanning = context;
            baseline = latest;
        }
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
                // Preserve the original corruption handling path; labels still transition to a
                // waiting state, but no guessed canonical document is written.
            }
        }

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
