using System.Security.Cryptography;
using System.Text;
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

        return mode == WorkflowMode.PlanOnly
            ? await RejectPlanOnlyAsync(config, issueNumber, currentState, cancellationToken).ConfigureAwait(false)
            : await RunFullAsync(config, issueNumber, currentState, omp, cancellationToken).ConfigureAwait(false);
    }
    public async Task<WorkflowOutcome> RejectPlanOnlyAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var restoredState = currentState with
        {
            Phase = WorkflowPhase.Planned,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.PlanApproval,
            UpdatedAt = deps.Clock.UtcNow,
        };
        var canonicalComment = await CanonicalCommentLocator
            .FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken, config.CanonicalCommentAuthor)
            .ConfigureAwait(false);
        if (canonicalComment is not null)
        {
            var content = CanonicalCommentMarkdown.Parse(canonicalComment.Body);
            await UpsertCanonicalCommentAsync(
                config,
                issueNumber,
                content with
                {
                    State = CanonicalStateSerializer.ToDocument(restoredState, content.State.PullOrMergeRequest),
                },
                cancellationToken).ConfigureAwait(false);
        }
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, [WorkflowCommand.Implement], cancellationToken).ConfigureAwait(false);
        await NotifyAsync(config, issueNumber, restoredState, WorkflowNotificationKind.HumanActionRequired,
            "This deployment is configured plan-only; implementation was not started. Approve manually or reconfigure to full mode.", cancellationToken)
            .ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, restoredState, "plan-only mode: implementation command rejected.");
    }

    private async Task<WorkflowOutcome> RunFullAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        IOmpClient omp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(omp);
        var canonicalComment = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken, config.CanonicalCommentAuthor).ConfigureAwait(false);
        if (canonicalComment is null)
        {
            return await EscalateWithoutCanonicalCommentAsync(
                config, issueNumber, currentState, "Cannot implement: no canonical comment was found for this issue.", cancellationToken).ConfigureAwait(false);
        }
        var existingContent = CanonicalCommentMarkdown.Parse(canonicalComment.Body);
        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);

        if (IsPlanStale(issue, existingContent))
        {
            var staleState = currentState with
            {
                Phase = WorkflowPhase.Planned,
                OperationalState = WorkflowOperationalState.Waiting,
                WaitingReason = WaitingReason.ReplanRequired,
                UpdatedAt = deps.Clock.UtcNow,
            };
            return await PauseAsync(
                config,
                issueNumber,
                staleState,
                existingContent,
                existingContent.ImplementationResult ?? string.Empty,
                WaitingReason.ReplanRequired,
                "The issue title or description changed since the approved plan, or the plan input hash is missing. Replan before implementing.",
                cancellationToken,
                commandsToConsume: [WorkflowCommand.Implement]).ConfigureAwait(false);
        }

        ProviderMergeRequest? linkedMergeRequest = null;
        if (currentState.PublicationStage == ImplementationPublicationStage.BranchPublished &&
            existingContent.State.PullOrMergeRequest is not null)
        {
            try
            {
                linkedMergeRequest = await StoredMergeRequestIdentity.FindAsync(
                    deps.Provider,
                    config.Repository,
                    existingContent.State.PullOrMergeRequest,
                    currentState.Branch,
                    currentState.TargetBranch,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (StoredMergeRequestIdentity.StoredMergeRequestUnavailableException exception)
            {
                return await PauseAsync(
                    config,
                    issueNumber,
                    currentState,
                    existingContent,
                    existingContent.ImplementationResult ?? string.Empty,
                    WaitingReason.ManualIntervention,
                    exception.Message,
                    cancellationToken,
                    [WorkflowCommand.Implement]).ConfigureAwait(false);
            }
            catch (CanonicalStateException exception)
            {
                return await PauseAsync(
                    config,
                    issueNumber,
                    currentState,
                    existingContent,
                    existingContent.ImplementationResult ?? string.Empty,
                    WaitingReason.CorruptState,
                    exception.Message,
                    cancellationToken,
                    [WorkflowCommand.Implement]).ConfigureAwait(false);
            }
        }

        if (currentState.Phase == WorkflowPhase.Implementing)
        {
            if (currentState.WaitingReason is WaitingReason.NewInputDuringImplementation or WaitingReason.MaterialPlanDeviation)
            {
                // The human explicitly asked to continue past a pause that intentionally kept the
                // worktree and its commits (specification §22, §25): resume in place rather than
                // resetting or redoing the implementation.
                return await ResumeAfterPauseAsync(config, issueNumber, currentState, existingContent, issue, omp, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentState.PublicationStage == ImplementationPublicationStage.ResultCheckpointed)
            {
                return await RecoverResultCheckpointedImplementationAsync(
                    config, issueNumber, currentState, existingContent, issue, cancellationToken).ConfigureAwait(false);
            }

            var recovered = await TryRecoverPublishedImplementationAsync(config, issueNumber, currentState, existingContent, cancellationToken)
                .ConfigureAwait(false);
            if (recovered is not null)
            {
                return recovered;
            }

            // No durable record of a completed, published implementation exists for this attempt:
            // never publish review for unverified work. Discard any partial local state and redo
            // implementation cleanly from the approved base commit below.
        }

        var approvedPlanRevision = existingContent.State.PlanRevision;
        var workingState = currentState with
        {
            Phase = WorkflowPhase.Implementing,
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
            ApprovedPlanRevision = approvedPlanRevision,
        };
        var workingContent = existingContent with
        {
            ImplementationResult = null,
            State = CanonicalStateSerializer.ToDocument(workingState, existingContent.State.PullOrMergeRequest),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, workingContent, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(
            config,
            issueNumber,
            WorkflowPhase.Implementing,
            WorkflowOperationalState.Working,
            currentState.Phase == WorkflowPhase.Planned ? [WorkflowCommand.Implement] : [WorkflowCommand.Continue],
            cancellationToken).ConfigureAwait(false);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        await deps.Git.ResetWorktreeAsync(config.Repository.Id, worktreePath, currentState.BaseCommit, cancellationToken).ConfigureAwait(false);

        var currentPlan = new PlanContext(existingContent.State.PlanRevision, existingContent.PlanText, existingContent.DecisionsAndRationale);
        var attachmentsPath = AttachmentsPath(config, currentState.WorkflowId);
        var (context, inputSnapshot) = await BuildContextForInputSnapshotAsync(
            config, issueNumber, workingState, currentPlan, linkedMergeRequest, attachmentsPath, cancellationToken).ConfigureAwait(false);
        if (IsPlanStale(inputSnapshot, existingContent))
        {
            return await PauseForStalePlanAsync(
                config, issueNumber, workingState, workingContent, string.Empty, cancellationToken,
                [WorkflowCommand.Implement]).ConfigureAwait(false);
        }
        await omp.SelectRoleAsync(config.ImplementationRole, cancellationToken).ConfigureAwait(false);
        var implementOutcome = await OmpRunCollector
            .RunToCompletionAsync(omp, new OmpRunRequest(currentState.OmpSessionId, worktreePath, config.ApplyInstructions(ImplementationPromptBuilder.BuildImplementationPrompt(context)), config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
            .ConfigureAwait(false);
        if (!implementOutcome.Succeeded)
        {
            return await FailAsync(config, issueNumber, workingState, implementOutcome.Error!.Message, cancellationToken).ConfigureAwait(false);
        }

        ImplementationResult result;
        try
        {
            result = ImplementationResult.Parse(implementOutcome.Completed!.ResultJson);
        }
        catch (WorkflowContractException exception)
        {
            return await FailAsync(config, issueNumber, workingState, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        if (result.IsMaterialDeviation)
        {
            return await PauseForMaterialDeviationAsync(config, issueNumber, workingState, workingContent, result, cancellationToken).ConfigureAwait(false);
        }

        return await PublishImplementationResultAsync(
            config, issueNumber, workingState, workingContent, worktreePath, issue, omp, linkedMergeRequest,
            result.RenderMarkdown(), inputSnapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes an implementation attempt the human explicitly asked to continue past a
    /// content-reviewed pause (new input, or an accepted material deviation). The retained worktree
    /// and its commits are never reset; a fresh input baseline is captured so the pre-publication
    /// gate can still catch input that arrives during this resumed turn.</summary>
    private async Task<WorkflowOutcome> ResumeAfterPauseAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        CanonicalCommentContent existingContent,
        ProviderIssue issue,
        IOmpClient omp,
        CancellationToken cancellationToken)
    {
        if (existingContent.ImplementationResult is not { Length: > 0 })
        {
            return await PauseAsync(
                config, issueNumber, currentState, existingContent, existingContent.ImplementationResult ?? string.Empty,
                WaitingReason.CorruptState,
                "Cannot continue: no implementation result was recorded for the paused attempt.",
                cancellationToken).ConfigureAwait(false);
        }

        var workingState = currentState with { OperationalState = WorkflowOperationalState.Working, WaitingReason = null, InterruptedPhase = null };
        var workingContent = existingContent with
        {
            State = CanonicalStateSerializer.ToDocument(workingState, existingContent.State.PullOrMergeRequest),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, workingContent, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Implementing, WorkflowOperationalState.Working, [WorkflowCommand.Continue], cancellationToken).ConfigureAwait(false);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        var inputSnapshot = await CaptureInputSnapshotAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
        await omp.SelectRoleAsync(config.ImplementationRole, cancellationToken).ConfigureAwait(false);
        var continuationOutcome = await OmpRunCollector
            .RunToCompletionAsync(
                omp,
                new OmpRunRequest(
                    workingState.OmpSessionId,
                    worktreePath,
                    config.ApplyInstructions(ImplementationPromptBuilder.BuildContinuationPrompt()),
                    config.OmpAllowedEnvironment,
                    config.OmpTimeout),
                cancellationToken).ConfigureAwait(false);
        if (!continuationOutcome.Succeeded)
        {
            return await FailAsync(config, issueNumber, workingState, continuationOutcome.Error!.Message, cancellationToken).ConfigureAwait(false);
        }

        ImplementationResult result;
        try
        {
            result = ImplementationResult.Parse(continuationOutcome.Completed!.ResultJson);
        }
        catch (WorkflowContractException exception)
        {
            return await FailAsync(config, issueNumber, workingState, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        if (result.IsMaterialDeviation)
        {
            return await PauseForMaterialDeviationAsync(config, issueNumber, workingState, workingContent, result, cancellationToken).ConfigureAwait(false);
        }

        return await PublishImplementationResultAsync(
            config, issueNumber, workingState, workingContent, worktreePath, issue, omp, null,
            result.RenderMarkdown(), inputSnapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shared publication tail: optional corrective pass, rebase/conflict resolution
    /// against the latest target, a final pre-push human-input check, the durable pre-publication
    /// checkpoint, LFS upload, push, and merge-request publication.</summary>
    private async Task<WorkflowOutcome> PublishImplementationResultAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent workingContent,
        string worktreePath,
        ProviderIssue issue,
        IOmpClient omp,
        ProviderMergeRequest? mergeRequest,
        string resultMarkdown,
        InputSnapshot inputSnapshot,
        CancellationToken cancellationToken)
    {
        if (await deps.Git.HasUncommittedChangesAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false))
        {
            var correctiveOutcome = await OmpRunCollector
                .RunToCompletionAsync(omp, new OmpRunRequest(workingState.OmpSessionId, worktreePath, config.ApplyInstructions(ImplementationPromptBuilder.BuildCorrectivePrompt()), config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
                .ConfigureAwait(false);
            if (!correctiveOutcome.Succeeded)
            {
                return await FailAsync(
                    config,
                    issueNumber,
                    workingState,
                    "Corrective implementation pass failed; publication was not attempted.",
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var correctiveResult = ImplementationResult.Parse(correctiveOutcome.Completed!.ResultJson);
                if (correctiveResult.IsMaterialDeviation)
                {
                    return await PauseForMaterialDeviationAsync(
                        config, issueNumber, workingState, workingContent, correctiveResult, cancellationToken).ConfigureAwait(false);
                }

                resultMarkdown = correctiveResult.RenderMarkdown();
            }
            catch (WorkflowContractException exception)
            {
                return await FailAsync(config, issueNumber, workingState, exception.Message, cancellationToken).ConfigureAwait(false);
            }

            if (await deps.Git.HasUncommittedChangesAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false))
            {
                return await FailAsync(
                    config,
                    issueNumber,
                    workingState,
                    "Corrective implementation pass left uncommitted changes; publication was not attempted.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await deps.Git.FetchAsync(config.Repository.Id, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        var latestTargetCommit = await deps.Git.ResolveBranchCommitAsync(config.Repository.Id, workingState.TargetBranch, cancellationToken).ConfigureAwait(false);
        if (latestTargetCommit != workingState.BaseCommit)
        {
            // A replan can return a published review to plan approval. Its base is the published
            // branch head, not the original target commit: merge target changes so the existing
            // PR/MR branch remains a fast-forward-only history.
            var integrated = workingState.PublicationStage == ImplementationPublicationStage.BranchPublished &&
                workingState.ExpectedImplementationHead is not null
                ? await deps.Git.TryMergeAsync(
                    config.Repository.Id, worktreePath, latestTargetCommit, config.GitIdentity, cancellationToken).ConfigureAwait(false)
                : await deps.Git.TryRebaseOntoAsync(
                    config.Repository.Id, worktreePath, latestTargetCommit, config.GitIdentity, cancellationToken).ConfigureAwait(false);
            if (!integrated)
            {
                await omp.SelectRoleAsync(config.ConflictResolutionRole, cancellationToken).ConfigureAwait(false);
                var conflictOutcome = await OmpRunCollector
                    .RunToCompletionAsync(omp, new OmpRunRequest(workingState.OmpSessionId, worktreePath, config.ApplyInstructions(ImplementationPromptBuilder.BuildConflictResolutionPrompt()), config.OmpAllowedEnvironment, config.OmpTimeout), cancellationToken)
                    .ConfigureAwait(false);
                if (!conflictOutcome.Succeeded)
                {
                    return await FailAsync(config, issueNumber, workingState, "Failed to resolve target-branch conflicts before publication.", cancellationToken).ConfigureAwait(false);
                }

                ImplementationResult conflictResult;
                try
                {
                    conflictResult = ImplementationResult.Parse(conflictOutcome.Completed!.ResultJson);
                }
                catch (WorkflowContractException exception)
                {
                    return await FailAsync(config, issueNumber, workingState, exception.Message, cancellationToken).ConfigureAwait(false);
                }

                if (conflictResult.IsMaterialDeviation)
                {
                    return await PauseForMaterialDeviationAsync(
                        config, issueNumber, workingState, workingContent, conflictResult, cancellationToken).ConfigureAwait(false);
                }

                resultMarkdown = conflictResult.RenderMarkdown();
            }
        }

        // Re-check human input immediately before publication. This gate covers the corrective pass
        // and conflict resolution above; the identical gate after LFS upload covers the final
        // upload window before the branch becomes visible to reviewers.
        var latestInputSnapshot = await CaptureInputSnapshotAsync(config, issueNumber, mergeRequest, cancellationToken).ConfigureAwait(false);
        if (IsPlanStale(latestInputSnapshot, workingContent))
        {
            return await PauseForStalePlanAsync(
                config, issueNumber, workingState, workingContent, resultMarkdown, cancellationToken).ConfigureAwait(false);
        }

        if (latestInputSnapshot != inputSnapshot)
        {
            return await PauseAsync(
                config,
                issueNumber,
                workingState,
                workingContent,
                resultMarkdown,
                WaitingReason.NewInputDuringImplementation,
                "New or edited human input arrived during implementation. The current worktree was retained; choose continue, replan, or cancel.",
                cancellationToken).ConfigureAwait(false);
        }

        if (await deps.Git.HasUncommittedChangesAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false))
        {
            return await FailAsync(
                config,
                issueNumber,
                workingState,
                "Worktree is not clean after target integration; publication was not attempted.",
                cancellationToken).ConfigureAwait(false);
        }

        var headCommit = await deps.Git.GetHeadCommitAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false);
        if (!await deps.Git.IsAncestorAsync(config.Repository.Id, latestTargetCommit, headCommit, cancellationToken).ConfigureAwait(false))
        {
            return await FailAsync(
                config,
                issueNumber,
                workingState,
                "Resolved worktree does not contain the latest target commit; publication was not attempted.",
                cancellationToken).ConfigureAwait(false);
        }

        // Durable pre-publication checkpoint: it binds the recorded result to this exact local
        // head, but explicitly does not claim the ref was published yet. Recovery may not infer a
        // successful push from a deterministic branch name or an older remote ref.
        var checkpointState = workingState with
        {
            ExpectedImplementationHead = headCommit,
            PublicationStage = ImplementationPublicationStage.ResultCheckpointed,
            ImplementationInputDigest = inputSnapshot.Digest,
            RebasedPublicationBase = latestTargetCommit,
        };
        var checkpointContent = workingContent with
        {
            ImplementationResult = resultMarkdown,
            State = CanonicalStateSerializer.ToDocument(checkpointState, workingContent.State.PullOrMergeRequest),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, checkpointContent, cancellationToken).ConfigureAwait(false);

        try
        {
            await deps.Git.PublishChangedSubmodulesAsync(
                config.Repository.Id,
                worktreePath,
                latestTargetCommit,
                workingState.Branch,
                config.GitAuthentication,
                config.SubmoduleAuthenticationResolver ?? (_ => null),
                cancellationToken).ConfigureAwait(false);

            if (deps.Git.WorktreeRequiresLfs(worktreePath))
            {
                await deps.Git.UploadLfsObjectsAsync(config.Repository.Id, worktreePath, workingState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException &&
            GitPublicationFailureClassifier.TryClassify(exception) is { } reason)
        {
            return await PauseAsync(
                config, issueNumber, checkpointState, checkpointContent, resultMarkdown, reason,
                GitPublicationFailureClassifier.Explanation(reason), cancellationToken).ConfigureAwait(false);
        }

        // LFS upload can take long enough for human input to arrive. Do not push a branch based on
        // work that was produced without that input.
        latestInputSnapshot = await CaptureInputSnapshotAsync(config, issueNumber, mergeRequest, cancellationToken).ConfigureAwait(false);
        if (IsPlanStale(latestInputSnapshot, workingContent))
        {
            return await PauseForStalePlanAsync(
                config, issueNumber, workingState, checkpointContent, resultMarkdown, cancellationToken).ConfigureAwait(false);
        }

        if (latestInputSnapshot != inputSnapshot)
        {
            return await PauseAsync(
                config,
                issueNumber,
                workingState,
                checkpointContent,
                resultMarkdown,
                WaitingReason.NewInputDuringImplementation,
                "New or edited human input arrived during LFS upload. The current worktree was retained; choose continue, replan, or cancel.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await deps.Git.PushAsync(config.Repository.Id, worktreePath, workingState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException &&
            GitPublicationFailureClassifier.TryClassify(exception) is { } reason)
        {
            return await PauseAsync(
                config, issueNumber, checkpointState, checkpointContent, resultMarkdown, reason,
                GitPublicationFailureClassifier.Explanation(reason), cancellationToken).ConfigureAwait(false);
        }

        // Only this persisted stage authorizes recovery to create a PR/MR. If this checkpoint is
        // absent after a crash, retain the result for human recovery rather than guessing whether
        // an identically named branch is this attempt's publication.
        var publishedBranchState = checkpointState with { PublicationStage = ImplementationPublicationStage.BranchPublished };
        checkpointContent = checkpointContent with
        {
            State = CanonicalStateSerializer.ToDocument(publishedBranchState, checkpointContent.State.PullOrMergeRequest),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, checkpointContent, cancellationToken).ConfigureAwait(false);

        var publishedMergeRequest = await FindOrCreateMergeRequestAsync(
            config, issueNumber, issue, publishedBranchState, checkpointContent.State.PullOrMergeRequest, resultMarkdown, cancellationToken).ConfigureAwait(false);

        return await PublishReviewAsync(
            config,
            issueNumber,
            publishedBranchState,
            checkpointContent,
            publishedMergeRequest,
            resultMarkdown,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes the idempotent publication tail after a durable result checkpoint. The
    /// checkpointed local head must still be intact; an exact remote match proves an interrupted
    /// push and can proceed directly to review publication, while a missing remote ref safely
    /// replays the LFS/submodule/push tail without rerunning OMP.</summary>
    private async Task<WorkflowOutcome> RecoverResultCheckpointedImplementationAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        CanonicalCommentContent existingContent,
        ProviderIssue issue,
        CancellationToken cancellationToken)
    {
        if (existingContent.ImplementationResult is not { Length: > 0 } implementationResult ||
            currentState.ExpectedImplementationHead is not { Length: > 0 } expectedHead ||
            currentState.ImplementationInputDigest is not { Length: > 0 } implementationInputDigest ||
            currentState.RebasedPublicationBase is not { Length: > 0 } rebasedPublicationBase)
        {
            return await PauseAsync(
                config,
                issueNumber,
                currentState,
                existingContent,
                existingContent.ImplementationResult ?? string.Empty,
                WaitingReason.CorruptState,
                "Cannot recover implementation publication: the result checkpoint is incomplete.",
                cancellationToken).ConfigureAwait(false);
        }

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        var localHead = await deps.Git.GetHeadCommitAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false);
        var worktreeDirty = await deps.Git.HasUncommittedChangesAsync(config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false);
        if (worktreeDirty || !string.Equals(localHead, expectedHead, StringComparison.Ordinal))
        {
            return await PauseAsync(
                config,
                issueNumber,
                currentState,
                existingContent,
                implementationResult,
                WaitingReason.ManualIntervention,
                "Cannot recover implementation publication: the retained worktree no longer matches the checkpointed implementation head.",
                cancellationToken).ConfigureAwait(false);
        }

        var recoveryInput = await CaptureInputSnapshotAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(recoveryInput.Digest, implementationInputDigest, StringComparison.Ordinal))
        {
            return await PauseAsync(
                config,
                issueNumber,
                currentState,
                existingContent,
                implementationResult,
                WaitingReason.NewInputDuringImplementation,
                "New or edited human input arrived since the implementation result was checkpointed. The current worktree was retained; choose continue, replan, or cancel.",
                cancellationToken).ConfigureAwait(false);
        }

        await deps.Git.FetchAsync(config.Repository.Id, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        var remoteHead = await deps.Git
            .TryResolveRemoteBranchCommitAsync(config.Repository.Id, currentState.Branch, cancellationToken)
            .ConfigureAwait(false);
        if (remoteHead is not null && !string.Equals(remoteHead, expectedHead, StringComparison.Ordinal))
        {
            return await PauseAsync(
                config,
                issueNumber,
                currentState,
                existingContent,
                implementationResult,
                WaitingReason.ManualIntervention,
                "Cannot recover implementation publication: the remote branch does not match the checkpointed implementation head.",
                cancellationToken).ConfigureAwait(false);
        }

        if (remoteHead is null)
        {
            // Recovery replays privileged publication work without rerunning OMP. Capture its own
            // baseline because human input may arrive after the original attempt was checkpointed.
            var inputSnapshot = await CaptureInputSnapshotAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
            try
            {
                await deps.Git.PublishChangedSubmodulesAsync(
                    config.Repository.Id,
                    worktreePath,
                    rebasedPublicationBase,
                    currentState.Branch,
                    config.GitAuthentication,
                    config.SubmoduleAuthenticationResolver ?? (_ => null),
                    cancellationToken).ConfigureAwait(false);
                if (deps.Git.WorktreeRequiresLfs(worktreePath))
                {
                    await deps.Git.UploadLfsObjectsAsync(
                        config.Repository.Id, worktreePath, currentState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException &&
                GitPublicationFailureClassifier.TryClassify(exception) is { } reason)
            {
                return await PauseAsync(
                    config, issueNumber, currentState, existingContent, implementationResult, reason,
                    GitPublicationFailureClassifier.Explanation(reason), cancellationToken).ConfigureAwait(false);
            }

            // Submodule publication and LFS upload can both take long enough for new human input
            // to arrive. Do not make the checkpointed branch visible until that input is reviewed.
            var latestInputSnapshot = await CaptureInputSnapshotAsync(config, issueNumber, cancellationToken).ConfigureAwait(false);
            if (IsPlanStale(latestInputSnapshot, existingContent))
            {
                return await PauseForStalePlanAsync(
                    config, issueNumber, currentState, existingContent, implementationResult, cancellationToken).ConfigureAwait(false);
            }

            if (latestInputSnapshot != inputSnapshot)
            {
                return await PauseAsync(
                    config,
                    issueNumber,
                    currentState,
                    existingContent,
                    implementationResult,
                    WaitingReason.NewInputDuringImplementation,
                    "New or edited human input arrived during publication recovery. The current worktree was retained; choose continue, replan, or cancel.",
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await deps.Git.PushAsync(
                    config.Repository.Id, worktreePath, currentState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException &&
                GitPublicationFailureClassifier.TryClassify(exception) is { } reason)
            {
                return await PauseAsync(
                    config, issueNumber, currentState, existingContent, implementationResult, reason,
                    GitPublicationFailureClassifier.Explanation(reason), cancellationToken).ConfigureAwait(false);
            }
        }

        var publishedState = currentState with { PublicationStage = ImplementationPublicationStage.BranchPublished };
        var publishedContent = existingContent with
        {
            State = CanonicalStateSerializer.ToDocument(publishedState, existingContent.State.PullOrMergeRequest),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, publishedContent, cancellationToken).ConfigureAwait(false);
        var mergeRequest = await FindOrCreateMergeRequestAsync(
            config, issueNumber, issue, publishedState, publishedContent.State.PullOrMergeRequest, implementationResult, cancellationToken).ConfigureAwait(false);
        return await PublishReviewAsync(
            config, issueNumber, publishedState, publishedContent, mergeRequest, implementationResult, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recovers an interrupted implementation attempt only after the canonical
    /// checkpoint proves this exact head was pushed. A deterministic branch name or a stale
    /// remote ref is not evidence that this attempt may create or recover a review request.</summary>
    private async Task<WorkflowOutcome?> TryRecoverPublishedImplementationAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        CanonicalCommentContent existingContent,
        CancellationToken cancellationToken)
    {
        if (existingContent.ImplementationResult is not { Length: > 0 } implementationResult)
        {
            return null;
        }

        if (currentState.PublicationStage != ImplementationPublicationStage.BranchPublished ||
            currentState.ExpectedImplementationHead is not { Length: > 0 } expectedHead)
        {
            return await PauseAsync(
                config,
                issueNumber,
                currentState,
                existingContent,
                implementationResult,
                WaitingReason.ManualIntervention,
                "Cannot recover implementation publication: the durable branch-published checkpoint is missing.",
                cancellationToken).ConfigureAwait(false);
        }

        await deps.Git.FetchAsync(config.Repository.Id, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        var remoteBranchHead = await deps.Git
            .TryResolveRemoteBranchCommitAsync(config.Repository.Id, currentState.Branch, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(remoteBranchHead, expectedHead, StringComparison.Ordinal))
        {
            return await PauseAsync(
                config,
                issueNumber,
                currentState,
                existingContent,
                implementationResult,
                WaitingReason.ManualIntervention,
                "Cannot recover implementation publication: the remote branch does not match the checkpointed implementation head.",
                cancellationToken).ConfigureAwait(false);
        }

        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var mergeRequest = await FindOrCreateMergeRequestAsync(
            config, issueNumber, issue, currentState, existingContent.State.PullOrMergeRequest, implementationResult, cancellationToken).ConfigureAwait(false);
        return await PublishReviewAsync(
            config,
            issueNumber,
            currentState,
            existingContent,
            mergeRequest,
            implementationResult,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowOutcome> PublishReviewAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState state,
        CanonicalCommentContent existingContent,
        ProviderMergeRequest mergeRequest,
        string implementationResult,
        CancellationToken cancellationToken)
    {
        var feedbackSnapshot = existingContent.State.ReviewFeedbackCutoff is { } existingCutoff
            ? new ReviewFeedbackSnapshot(
                existingCutoff,
                new HashSet<string>(existingContent.State.ReviewFeedbackVersions ?? [], StringComparer.Ordinal))
            : new ReviewFeedbackSnapshot(deps.Clock.UtcNow, new HashSet<string>(StringComparer.Ordinal));
        var stateWithFeedbackSnapshot = state with
        {
            ReviewFeedbackCutoff = feedbackSnapshot.Cutoff,
            ReviewFeedbackVersions = feedbackSnapshot.Versions,
        };

        // This records the start of review, not feedback that happened to be visible while the
        // draft was being published. No review feedback has been processed at this point, so its
        // version tokens must not be checkpointed as handled. Recovery reuses an existing boundary.
        var checkpointContent = existingContent with
        {
            State = CanonicalStateSerializer.ToDocument(
                stateWithFeedbackSnapshot,
                $"{config.Repository.Id}#{mergeRequest.Number}"),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, checkpointContent, cancellationToken).ConfigureAwait(false);

        var publishedAt = deps.Clock.UtcNow;
        var publishedState = stateWithFeedbackSnapshot with
        {
            Phase = WorkflowPhase.Review,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ReviewRequested,
            InterruptedPhase = null,
            UpdatedAt = publishedAt,
        };
        var content = checkpointContent with
        {
            ImplementationResult = $"{implementationResult.TrimEnd()}\n\n## Linked pull/merge request {FormatMergeRequestReference(mergeRequest)}",
            State = CanonicalStateSerializer.ToDocument(
                publishedState,
                $"{config.Repository.Id}#{mergeRequest.Number}"),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, content, cancellationToken).ConfigureAwait(false);
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Review, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await NotifyAsync(
            config,
            issueNumber,
            publishedState,
            WorkflowNotificationKind.ImplementationReady,
            "Draft PR/MR is ready for review.",
            cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, publishedState, "Implementation published; awaiting review.");
    }

    private sealed record ReviewFeedbackSnapshot(DateTimeOffset Cutoff, IReadOnlySet<string> Versions);

    private async Task<ProviderMergeRequest> FindOrCreateMergeRequestAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderIssue issue,
        WorkflowState state,
        string? durableMergeRequestIdentity,
        string implementationResult,
        CancellationToken cancellationToken)
    {
        var existing = await FindMergeRequestAsync(
            config, state, durableMergeRequestIdentity, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!HasWorkflowMarker(existing, state))
            {
                throw new WorkflowContractException(
                    "The stored or branch-matched merge request is not marked as belonging to this workflow.");
            }

            return existing;
        }

        var closingReference = config.CloseIssueOnMerge
            ? Environment.NewLine + Environment.NewLine +
                (string.Equals(deps.Provider.Name, "gitlab", StringComparison.OrdinalIgnoreCase)
                    ? $"Closes #{issueNumber}"
                    : $"Fixes #{issueNumber}")
            : string.Empty;
        var body = $"""
            <!-- issue-agent:workflow:{state.WorkflowId} -->
            ## IssueAgent implementation

            {implementationResult.Trim()}{closingReference}
            """.Trim();
        var created = await deps.Provider.CreateDraftMergeRequestAsync(
            new CreateMergeRequestRequest(config.Repository, state.Branch, state.TargetBranch, issue.Title, body, IsDraft: true, issueNumber),
            cancellationToken).ConfigureAwait(false);
        var adopted = await FindMergeRequestAsync(
            config, state, durableMergeRequestIdentity: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (adopted is null || !HasWorkflowMarker(adopted, state))
        {
            throw new WorkflowContractException(
                $"Provider did not return the newly-created merge request #{created.Number} with this workflow's marker.");
        }

        return adopted;
    }

    private async Task<ProviderMergeRequest?> FindMergeRequestAsync(
        WorkflowRepositoryConfig config,
        WorkflowState state,
        string? durableMergeRequestIdentity,
        CancellationToken cancellationToken)
    {
        if (durableMergeRequestIdentity is not null)
        {
            var expectedIdentity = $"{config.Repository.Id}#";
            if (!durableMergeRequestIdentity.StartsWith(expectedIdentity, StringComparison.Ordinal) ||
                !long.TryParse(durableMergeRequestIdentity[expectedIdentity.Length..], out var number) ||
                number <= 0)
            {
                throw new WorkflowContractException("The stored pull/merge-request identity is invalid.");
            }

            return await deps.Provider.GetMergeRequestAsync(config.Repository, number, cancellationToken).ConfigureAwait(false);
        }

        // Before the canonical publication checkpoint records the request number, a marker is the
        // only durable proof that a branch-matched request belongs to this workflow.
        return await deps.Provider.FindMergeRequestAsync(
            config.Repository, state.Branch, state.TargetBranch, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasWorkflowMarker(ProviderMergeRequest mergeRequest, WorkflowState state) =>
        mergeRequest.Description.Contains($"<!-- issue-agent:workflow:{state.WorkflowId} -->", StringComparison.Ordinal);

    private static string FormatMergeRequestReference(ProviderMergeRequest mergeRequest) =>
        mergeRequest.WebUrl is { IsAbsoluteUri: true } webUrl
            ? $"[#{mergeRequest.Number}]({webUrl.AbsoluteUri})"
            : $"#{mergeRequest.Number}";

    /// <summary>The plan is stale when the issue's title/description content no longer matches
    /// what the currently published plan was written against. A missing hash is also stale rather
    /// than being treated as a fresh plan (specification §22).</summary>

    private static bool IsPlanStale(ProviderIssue issue, CanonicalCommentContent existingContent) =>
        IsPlanStale(new InputSnapshot(issue.Title, issue.Description, string.Empty), existingContent);

    private static bool IsPlanStale(InputSnapshot input, CanonicalCommentContent existingContent) =>
        existingContent.State.PlanInputHash is not { Length: > 0 } hash ||
        !string.Equals(hash, PlanInputHasher.Compute(input.Title, input.Description), StringComparison.Ordinal);

    private Task<WorkflowOutcome> PauseForStalePlanAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent existingContent,
        string implementationResult,
        CancellationToken cancellationToken,
        IReadOnlyCollection<WorkflowCommand>? commandsToConsume = null) =>
        PauseAsync(
            config,
            issueNumber,
            workingState with
            {
                Phase = WorkflowPhase.Planned,
                OperationalState = WorkflowOperationalState.Waiting,
                WaitingReason = WaitingReason.ReplanRequired,
            },
            existingContent,
            implementationResult,
            WaitingReason.ReplanRequired,
            "The issue title or description changed since the approved plan, or the plan input hash is missing. Replan before implementing.",
            cancellationToken,
            commandsToConsume);

    private Task<WorkflowOutcome> PauseForMaterialDeviationAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent existingContent,
        ImplementationResult result,
        CancellationToken cancellationToken) =>
        PauseAsync(
            config,
            issueNumber,
            workingState,
            existingContent,
            result.RenderMarkdown(),
            WaitingReason.MaterialPlanDeviation,
            result.MaterialDeviationExplanation
                ?? "OMP identified a material deviation from the approved plan and paused before proceeding.",
            cancellationToken);

    private async Task<WorkflowOutcome> PauseAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        CanonicalCommentContent existingContent,
        string implementationResult,
        WaitingReason reason,
        string message,
        CancellationToken cancellationToken,
        IReadOnlyCollection<WorkflowCommand>? commandsToConsume = null)
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
        await TransitionLabelsAsync(
            config,
            issueNumber,
            pausedState.Phase,
            WorkflowOperationalState.Waiting,
            commandsToConsume ?? [],
            cancellationToken).ConfigureAwait(false);
        await NotifyAsync(config, issueNumber, pausedState, WorkflowNotificationKind.HumanActionRequired, message, cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, pausedState, message);
    }

    /// <summary>Preserves state and escalates to a human instead of throwing an unhandled exception
    /// when the canonical comment itself cannot be located (specification §25); nothing exists yet
    /// to update, so this only transitions labels and notifies.</summary>
    private async Task<WorkflowOutcome> EscalateWithoutCanonicalCommentAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState currentState,
        string message,
        CancellationToken cancellationToken)
    {
        var pausedState = currentState with
        {
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.CorruptState,
            UpdatedAt = deps.Clock.UtcNow,
        };
        await TransitionLabelsAsync(config, issueNumber, pausedState.Phase, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await NotifyAsync(config, issueNumber, pausedState, WorkflowNotificationKind.HumanActionRequired, message, cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, pausedState, message);
    }


    /// <summary>Captures implementation input before prompt construction. When input changes while
    /// context is assembled, rebuild the prompt context and capture a final baseline for the
    /// pre-prompt plan-staleness and pre-push input gates.</summary>
    private async Task<(AgentContext Context, InputSnapshot InputSnapshot)> BuildContextForInputSnapshotAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        WorkflowState workingState,
        PlanContext currentPlan,
        ProviderMergeRequest? mergeRequest,
        string attachmentsPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var baseline = await CaptureInputSnapshotAsync(config, issueNumber, mergeRequest, cancellationToken).ConfigureAwait(false);
            var context = await deps.ContextBuilder
                .BuildAsync(config.Repository, issueNumber, workingState, currentPlan, mergeRequest, attachmentsPath, cancellationToken)
                .ConfigureAwait(false);
            var reconciled = await CaptureInputSnapshotAsync(config, issueNumber, mergeRequest, cancellationToken).ConfigureAwait(false);
            if (reconciled == baseline)
            {
                return (context, baseline);
            }
        }
    }

    private sealed record InputSnapshot(string Title, string Description, string CommentsDigest, string MergeRequestDigest = "")
    {
        public string Digest => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(MergeRequestDigest.Length == 0
                ? $"{Title}\0{Description}\0{CommentsDigest}"
                : $"{Title}\0{Description}\0{CommentsDigest}\0{MergeRequestDigest}")));
    }

    private async Task<InputSnapshot> CaptureInputSnapshotAsync(
        WorkflowRepositoryConfig config,
        long issueNumber,
        ProviderMergeRequest? mergeRequest,
        CancellationToken cancellationToken)
    {
        var issue = await deps.Provider.GetIssueAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var commentStamps = new List<string>();
        string? authoritativeAuthor = null;
        await foreach (var comment in deps.Provider.GetIssueCommentsAsync(config.Repository, issueNumber, cancellationToken).ConfigureAwait(false))
        {
            var isAuthoritativeCanonicalComment = CanonicalCommentMarkdown.IsCanonicalComment(comment.Body) &&
                CanonicalCommentMarkdown.IsAuthoritativeCanonicalComment(comment, authoritativeAuthor ??= await CanonicalCommentLocator
                    .ResolveAuthoritativeIdentityAsync(deps.Provider, config.CanonicalCommentAuthor, cancellationToken).ConfigureAwait(false));
            if ((!config.IgnoreBotComments || !comment.IsBot) && !isAuthoritativeCanonicalComment)
            {
                commentStamps.Add($"{comment.Id}:{comment.UpdatedAt:O}:{comment.Body}");
            }
        }

        var requestStamps = new List<string>();
        if (mergeRequest is not null)
        {
            requestStamps.Add($"{mergeRequest.Number}:{mergeRequest.Title}:{mergeRequest.Description}");
            await foreach (var comment in deps.Provider.GetMergeRequestCommentsAsync(config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false))
            {
                if (!config.IgnoreBotComments || !comment.IsBot)
                {
                    requestStamps.Add($"comment:{comment.Id}:{comment.UpdatedAt:O}:{comment.Body}");
                }
            }
            await foreach (var thread in deps.Provider.GetReviewThreadsAsync(config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false))
            {
                requestStamps.Add($"thread:{thread.Id}:{thread.IsResolved}");
                foreach (var comment in thread.Comments)
                {
                    if (!config.IgnoreBotComments || !comment.IsBot)
                    {
                        requestStamps.Add($"thread:{thread.Id}:{comment.Id}:{comment.UpdatedAt:O}:{comment.Body}");
                    }
                }
            }
        }

        return new InputSnapshot(
            issue.Title,
            issue.Description,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', commentStamps)))),
            mergeRequest is null
                ? string.Empty
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', requestStamps)))));
    }

    private Task<InputSnapshot> CaptureInputSnapshotAsync(WorkflowRepositoryConfig config, long issueNumber, CancellationToken cancellationToken) =>
        CaptureInputSnapshotAsync(config, issueNumber, mergeRequest: null, cancellationToken);

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
                // Do not overwrite an already-corrupt canonical document with guessed content.
            }
        }
        await TransitionLabelsAsync(config, issueNumber, WorkflowPhase.Failed, WorkflowOperationalState.Waiting, [], cancellationToken).ConfigureAwait(false);
        await NotifyAsync(config, issueNumber, failedState, WorkflowNotificationKind.ImplementationFailed, message, cancellationToken).ConfigureAwait(false);
        return new WorkflowOutcome(WorkflowOutcomeStatus.Failed, failedState, message);
    }

    private Task NotifyAsync(WorkflowRepositoryConfig config, long issueNumber, WorkflowState state, WorkflowNotificationKind kind, string message, CancellationToken cancellationToken) =>
        deps.Notifier.NotifyAsync(new WorkflowNotification(kind, config.Repository.Id, issueNumber, state.WorkflowId.ToString(), message), cancellationToken);

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
