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
    /// <summary>Creates the durable identity checkpoint for a first planning attempt before any
    /// retained worktree or OMP session exists.</summary>
    public async Task<WorkflowState> CreateInitialCheckpointAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        CancellationToken cancellationToken,
        WorkflowId? suppliedWorkflowId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var workflowId = suppliedWorkflowId ?? WorkflowId.New();
        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var targetBranch = config.TargetBranchOverride;
        var baseCommit = await deps.Git.ResolveBranchCommitAsync(config.Repository.Id, targetBranch, cancellationToken).ConfigureAwait(false);
        var initialState = new WorkflowState(
            workflowId, WorkflowPhase.Planning, WorkflowOperationalState.Working, null,
            PlanRevision: 0, ApprovedPlanRevision: null, OmpSessionId: string.Empty,
            BranchNaming.DeriveBranchName(issueNumber, issue.Title), targetBranch, baseCommit,
            deps.Clock.UtcNow, PlanInputHasher.Compute(issue.Title, issue.Description));

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
        return initialState;
    }
    /// <summary>Creates or validates the retained worktree required as OMP's startup directory
    /// for initial planning. The checkpoint is durable before this side effect, so a process
    /// failure can safely retry the same operation during bootstrap recovery.</summary>
    public async Task<string> EnsureInitialPlanningWorktreeAsync(
        WorkflowRepositoryConfig config,
        WorkflowState initialCheckpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(initialCheckpoint);
        var worktreePath = WorktreePath(config, initialCheckpoint.WorkflowId);
        await deps.Git.CreateWorktreeAsync(
            config.Repository.Id,
            initialCheckpoint.WorkflowId.ToString(),
            worktreePath,
            initialCheckpoint.Branch,
            initialCheckpoint.BaseCommit,
            cancellationToken).ConfigureAwait(false);
        return worktreePath;
    }
    /// <summary>Restores a failed pre-session bootstrap to its durable initial-planning
    /// checkpoint after a human explicitly acknowledges the infrastructure failure.</summary>
    public async Task<WorkflowState> ResumeInitialPlanningBootstrapAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState failedBootstrapState,
        CancellationToken cancellationToken)
    {
        var initialState = failedBootstrapState with
        {
            Phase = WorkflowPhase.Planning,
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
            OmpSessionId = string.Empty,
            OmpSessionFile = null,
            InterruptedPhase = null,
            UpdatedAt = deps.Clock.UtcNow,
        };
        await UpsertCanonicalCommentAsync(
            config,
            issueNumber,
            new CanonicalCommentContent(
                "Planning is in progress.",
                [],
                null,
                CanonicalStateSerializer.ToDocument(initialState, pullOrMergeRequest: null)),
            cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(
            config,
            issueNumber,
            WorkflowPhase.Planning,
            WorkflowOperationalState.Working,
            [WorkflowCommand.Continue],
            cancellationToken).ConfigureAwait(false);
        return initialState;
    }



    /// <summary>Runs initial planning from its durable identity checkpoint.</summary>
    public async Task<WorkflowOutcome> RunInitialPlanningAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        IOmpClient omp,
        CancellationToken cancellationToken,
        WorkflowId? suppliedWorkflowId = null,
        WorkflowState? initialCheckpoint = null,
        string? initialWorktreePath = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(omp);
        var initialState = initialCheckpoint
            ?? await CreateInitialCheckpointAsync(config, issueNumber, cancellationToken, suppliedWorkflowId).ConfigureAwait(false);
        Activity.Current?.SetTag("WorkflowId", initialState.WorkflowId.ToString());

        var worktreePath = initialWorktreePath
            ?? await EnsureInitialPlanningWorktreeAsync(config, initialState, cancellationToken).ConfigureAwait(false);
        if (initialState.OmpSessionFile is null)
        {
            var session = await omp.CreateSessionAsync(config.PlanningRole, cancellationToken).ConfigureAwait(false);
            initialState = initialState with { OmpSessionId = session.SessionId, OmpSessionFile = session.SessionFile };
            await UpsertCanonicalCommentAsync(
                config,
                issueNumber,
                new CanonicalCommentContent(
                    "Planning is in progress.",
                    [],
                    null,
                    CanonicalStateSerializer.ToDocument(initialState, pullOrMergeRequest: null)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await omp.ResumeSessionAsync(initialState.OmpSessionId, initialState.OmpSessionFile, cancellationToken).ConfigureAwait(false);
        }

        await PrepareWorktreeContentAsync(config, worktreePath, cancellationToken).ConfigureAwait(false);
        var planningInput = await CaptureInputSnapshotAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
        var attachmentsPath = AttachmentsPath(config, initialState.WorkflowId);
        var context = await deps.ContextBuilder
            .BuildAsync(config.Repository, issueNumber, initialState, currentPlan: null, mergeRequest: null, attachmentsPath, cancellationToken)
            .ConfigureAwait(false);

        var prompt = config.ApplyInstructions(PlanningPromptBuilder.BuildInitialPlanPrompt(context));
        await omp.SelectRoleAsync(config.PlanningRole, cancellationToken).ConfigureAwait(false);
        var outcome = await OmpRunCollector
            .RunToCompletionAsync(omp, new OmpRunRequest(initialState.OmpSessionId, worktreePath, prompt, config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
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
                config, issueNumber, omp, initialState.OmpSessionId, worktreePath, context, planningResult, planningInput, currentPlan: null, cancellationToken)
                .ConfigureAwait(false);
            return await PublishPlanAsync(
                config,
                issueNumber,
                initialState,
                reconciledResult.Result,
                planRevision: 1,
                reconciledResult.Input.Title,
                reconciledResult.Input.Description,
                new CanonicalCommentContent(
                    string.Empty,
                    [],
                    null,
                    CanonicalStateSerializer.ToDocument(initialState, pullOrMergeRequest: null)),
                preservesPublishedReview: false,
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

        var canonicalComment = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken, config.CanonicalCommentAuthor).ConfigureAwait(false);
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
        await TransitionLabelsAsync(
            config,
            issueNumber,
            WorkflowPhase.Planning,
            WorkflowOperationalState.Working,
            currentState.Phase is WorkflowPhase.Planned or WorkflowPhase.Review
                ? [WorkflowCommand.Replan]
                : [WorkflowCommand.Continue],
            cancellationToken).ConfigureAwait(false);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        var attachmentsPath = AttachmentsPath(config, currentState.WorkflowId);
        await PrepareWorktreeContentAsync(config, worktreePath, cancellationToken).ConfigureAwait(false);
        var currentPlan = new PlanContext(existingContent.State.PlanRevision, existingContent.PlanText, existingContent.DecisionsAndRationale);

        var planningInput = await CaptureInputSnapshotAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
        var context = await deps.ContextBuilder
            .BuildAsync(config.Repository, issueNumber, workingState, currentPlan, mergeRequest: null, attachmentsPath, cancellationToken)
            .ConfigureAwait(false);

        var feedback = context.PrimaryIssue.HumanComments
            .Where(c => c.CreatedAt > existingContent.State.UpdatedAt)
            .ToList();

        var prompt = config.ApplyInstructions(PlanningPromptBuilder.BuildReplanPrompt(context, feedback));
        await omp.SelectRoleAsync(config.PlanningRole, cancellationToken).ConfigureAwait(false);
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
                existingContent,
                currentState.Phase == WorkflowPhase.Review &&
                    currentState.PublicationStage == ImplementationPublicationStage.BranchPublished &&
                    currentState.ExpectedImplementationHead is not null,
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
        WorkflowRepositoryConfig config,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var commentStamps = new List<string>();
        string? authoritativeAuthor = null;
        await foreach (var comment in deps.Provider.GetIssueCommentsAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            var isAuthoritativeCanonicalComment = CanonicalCommentMarkdown.IsCanonicalComment(comment.Body) &&
                CanonicalCommentMarkdown.IsAuthoritativeCanonicalComment(
                    comment,
                    authoritativeAuthor ??= await CanonicalCommentLocator
                        .ResolveAuthoritativeIdentityAsync(deps.Provider, config.CanonicalCommentAuthor, cancellationToken)
                        .ConfigureAwait(false));
            if ((!config.IgnoreBotComments || !comment.IsBot) && !isAuthoritativeCanonicalComment)
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
            var latest = await CaptureInputSnapshotAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
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
            await omp.SelectRoleAsync(config.PlanningRole, cancellationToken).ConfigureAwait(false);
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
        CanonicalCommentContent existingContent,
        bool preservesPublishedReview,
        CancellationToken cancellationToken)
    {
        var suggestedBranch = preservesPublishedReview
            ? null
            : workingState.PendingBranch
                ?? BranchNaming.TryDeriveSuggestedBranchName(issueNumber, issueTitle, planningResult.SuggestedSlug);
        if (suggestedBranch is not null && !string.Equals(workingState.Branch, suggestedBranch, StringComparison.Ordinal))
        {
            if (workingState.PendingBranch is null)
            {
                // The desired branch must be durable before Git changes the retained worktree.
                // A retry can then safely distinguish a rename that has not started from one that
                // completed before its final checkpoint.
                workingState = workingState with { PendingBranch = suggestedBranch, UpdatedAt = deps.Clock.UtcNow };
                await CheckpointSuggestedBranchAsync(config, issueNumber, workingState, cancellationToken).ConfigureAwait(false);
            }

            await deps.Git.RenameWorktreeBranchAsync(
                config.Repository.Id,
                WorktreePath(config, workingState.WorkflowId),
                workingState.Branch,
                suggestedBranch,
                cancellationToken).ConfigureAwait(false);

            workingState = workingState with
            {
                Branch = suggestedBranch,
                PendingBranch = null,
                UpdatedAt = deps.Clock.UtcNow,
            };
            await CheckpointSuggestedBranchAsync(config, issueNumber, workingState, cancellationToken).ConfigureAwait(false);
        }

        // OMP's planning contract is read-only. Discard any accidental planning-time edits. A
        // replan from review resumes on the published branch head so its next implementation can
        // fast-forward the existing PR/MR branch instead of resetting it to the original base.
        var implementationBase = preservesPublishedReview
            ? workingState.ExpectedImplementationHead!
            : workingState.BaseCommit;
        await deps.Git.ResetWorktreeAsync(
            config.Repository.Id,
            WorktreePath(config, workingState.WorkflowId),
            implementationBase,
            cancellationToken).ConfigureAwait(false);

        var publishedState = workingState with
        {
            Phase = WorkflowPhase.Planned,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.PlanApproval,
            PlanRevision = planRevision,
            BaseCommit = implementationBase,
            UpdatedAt = deps.Clock.UtcNow,
            PlanInputHash = PlanInputHasher.Compute(issueTitle, issueDescription),
        };

        var content = new CanonicalCommentContent(
            planningResult.PlanText,
            planningResult.DecisionsAndRationale,
            ImplementationResult: preservesPublishedReview ? existingContent.ImplementationResult : null,
            CanonicalStateSerializer.ToDocument(
                publishedState,
                preservesPublishedReview ? existingContent.State.PullOrMergeRequest : null));

        await UpsertCanonicalCommentAsync(config, issueNumber, content, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, [], cancellationToken)
            .ConfigureAwait(false);
        await deps.Notifier.NotifyAsync(
            new WorkflowNotification(WorkflowNotificationKind.PlanReady, config.Repository.Id, issueNumber, publishedState.WorkflowId.ToString(), "Plan is ready for review."),
            cancellationToken).ConfigureAwait(false);

        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, publishedState, "Plan published; awaiting human approval.");
    }

    private async Task CheckpointSuggestedBranchAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        var canonical = await CanonicalCommentLocator
            .FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken, config.CanonicalCommentAuthor)
            .ConfigureAwait(false);
        if (canonical is null)
        {
            throw new WorkflowContractException("Cannot checkpoint suggested branch: no canonical comment was found.");
        }

        var existingContent = CanonicalCommentMarkdown.Parse(canonical.Body);
        await UpsertCanonicalCommentAsync(
            config,
            issueNumber,
            existingContent with
            {
                State = CanonicalStateSerializer.ToDocument(state, existingContent.State.PullOrMergeRequest),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Durably stops bootstrap recovery after the idempotent retained-worktree setup
    /// exhausted its configured retry budget.</summary>
    public Task<WorkflowOutcome> FailInitialPlanningBootstrapAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState initialCheckpoint,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return FailAsync(
            config,
            issueNumber,
            initialCheckpoint,
            WaitingReason.ManualIntervention,
            $"Initial planning could not create its retained worktree after bounded retries ({exception.GetType().Name}). The durable checkpoint was preserved; resolve the local Git/worktree problem before continuing.",
            cancellationToken);
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
            InterruptedPhase = workingState.Phase,
            UpdatedAt = deps.Clock.UtcNow,
        };
        var canonical = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken, config.CanonicalCommentAuthor).ConfigureAwait(false);
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
        var existing = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken, config.CanonicalCommentAuthor).ConfigureAwait(false);
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
        await LabelCatalog.EnsureAllAsync(deps.Provider, config.Repository, cancellationToken).ConfigureAwait(false);
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
        await deps.Git.MaterializeLfsContentAsync(
            config.Repository.Id,
            worktreePath,
            config.GitAuthentication,
            submoduleAuthenticationResolver,
            cancellationToken).ConfigureAwait(false);
    }

    private static string WorktreePath(WorkflowRepositoryConfig config, WorkflowId workflowId) =>
        Path.Combine(config.WorkflowsStoragePath, workflowId.ToString(), "worktree");

    private static string AttachmentsPath(WorkflowRepositoryConfig config, WorkflowId workflowId) =>
        Path.Combine(config.WorkflowsStoragePath, workflowId.ToString(), "attachments");
}
