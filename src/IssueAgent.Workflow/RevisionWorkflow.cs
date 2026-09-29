using System.Security.Cryptography;
using System.Text;
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

        var canonicalComment = await CanonicalCommentLocator.FindAsync(deps.Provider, config.Repository, issueNumber, cancellationToken, config.CanonicalCommentAuthor).ConfigureAwait(false);
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
        await TransitionLabelsAsync(
            config,
            issueNumber,
            WorkflowPhase.Revising,
            WorkflowOperationalState.Working,
            currentState.Phase == WorkflowPhase.Review ? [WorkflowCommand.Revise] : [WorkflowCommand.Continue],
            cancellationToken).ConfigureAwait(false);
        ProviderMergeRequest? mergeRequest;
        try
        {
            mergeRequest = await StoredMergeRequestIdentity.FindAsync(
                deps.Provider,
                config.Repository,
                existingContent.State.PullOrMergeRequest,
                currentState.Branch,
                currentState.TargetBranch,
                cancellationToken).ConfigureAwait(false);
        }
        catch (StoredMergeRequestIdentity.StoredMergeRequestUnavailableException exception)
        {
            return await PauseForNewFeedbackAsync(
                config,
                issueNumber,
                workingState,
                existingContent,
                existingContent.ImplementationResult ?? string.Empty,
                exception.Message,
                cancellationToken,
                WaitingReason.ManualIntervention).ConfigureAwait(false);
        }
        catch (ProviderResourceNotFoundException)
        {
            return await PauseForNewFeedbackAsync(
                config,
                issueNumber,
                workingState,
                existingContent,
                existingContent.ImplementationResult ?? string.Empty,
                "The pull/merge request recorded by this workflow no longer exists. Local workflow data was preserved for human review.",
                cancellationToken,
                WaitingReason.ManualIntervention).ConfigureAwait(false);
        }
        catch (CanonicalStateException exception)
        {
            return await EscalateAsync(config, issueNumber, workingState, existingContent, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        if (mergeRequest is null)
        {
            return await EscalateAsync(
                config, issueNumber, workingState, existingContent, "Cannot revise: no merge request was found for this workflow's branch.", cancellationToken).ConfigureAwait(false);
        }

        if (mergeRequest.IsMerged)
        {
            return await new CancellationWorkflow(deps)
                .CompleteOnMergeAsync(config, issueNumber, currentState, existingContent, cancellationToken)
                .ConfigureAwait(false);
        }

        if (mergeRequest.IsClosed)
        {
            return await new CancellationWorkflow(deps)
                .CompleteOnCloseWithoutMergeAsync(config, issueNumber, currentState, existingContent, cancellationToken)
                .ConfigureAwait(false);
        }

        await ConsumeMergeRequestCommandAsync(
            config,
            mergeRequest.Number,
            currentState.Phase == WorkflowPhase.Review ? WorkflowCommand.Revise : WorkflowCommand.Continue,
            cancellationToken).ConfigureAwait(false);
        if (retainedResult is { Length: > 0 })
        {
            if (await HasUnseenFeedbackSinceCheckpointAsync(
                    config,
                    mergeRequest.Number,
                    existingContent.State.ReviewFeedbackCutoff.Value,
                    existingContent.State.ReviewFeedbackVersions,
                    cancellationToken).ConfigureAwait(false))
            {
                return await PauseForNewFeedbackAsync(
                    config,
                    issueNumber,
                    workingState,
                    workingContent,
                    retainedResult,
                    "Unprocessed review feedback was found while recovering a retained revision. The retained worktree was preserved; review the feedback and request another revision.",
                    cancellationToken).ConfigureAwait(false);
            }

            var feedbackBeforePublication = await CaptureFeedbackSnapshotAsync(config, config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false);
            return await PublishRevisionAsync(
                config, issueNumber, workingState, workingContent, retainedResult, mergeRequest, feedbackBeforePublication, omp, cancellationToken)
                .ConfigureAwait(false);
        }
        var feedbackBeforeRevision = await CaptureFeedbackSnapshotAsync(config, config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false);
        var currentPlan = new PlanContext(existingContent.State.PlanRevision, existingContent.PlanText, existingContent.DecisionsAndRationale);

        var worktreePath = WorktreePath(config, currentState.WorkflowId);
        var attachmentsPath = AttachmentsPath(config, currentState.WorkflowId);
        var context = await deps.ContextBuilder
            .BuildAsync(config.Repository, issueNumber, workingState, currentPlan, mergeRequest, attachmentsPath, cancellationToken)
            .ConfigureAwait(false);

        var reviewFeedbackCutoff = existingContent.State.ReviewFeedbackCutoff.Value;
        var observedFeedbackVersions = existingContent.State.ReviewFeedbackVersions is { } versions
            ? new HashSet<string>(versions, StringComparer.Ordinal)
            : null;
        var feedback = context.PullOrMergeRequest is { } mrContext
            ? mrContext.Comments.Concat(mrContext.ReviewThreads)
                .Where(comment => IsNewFeedbackSinceCheckpoint(comment, reviewFeedbackCutoff, observedFeedbackVersions))
                .ToList()
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
        FeedbackSnapshot feedbackBeforeRevision,
        IOmpClient omp,
        CancellationToken cancellationToken)
    {
        mergeRequest = await StoredMergeRequestIdentity.FindAsync(
            deps.Provider,
            config.Repository,
            workingContent.State.PullOrMergeRequest,
            workingState.Branch,
            workingState.TargetBranch,
            cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowContractException("Cannot publish revision: no merge request was found for this workflow's branch.");

        // The durable checkpoint must bind the result to the final post-merge head. Recording it
        // earlier could authorize recovery of a different revision after target integration.
        var publicationCheckpoint = workingContent with { ImplementationResult = resultMarkdown };

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
            publicationCheckpoint = workingContent with { ImplementationResult = resultMarkdown };
        }

        var feedbackAfterRevision = await CaptureFeedbackSnapshotAsync(config, config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false);
        if (!feedbackAfterRevision.Versions.SetEquals(feedbackBeforeRevision.Versions))
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

        publicationCheckpoint = publicationCheckpoint with
        {
            State = CanonicalStateSerializer.ToDocument(
                workingState with { ExpectedImplementationHead = headCommit },
                workingContent.State.PullOrMergeRequest),
        };
        await UpsertCanonicalCommentAsync(config, issueNumber, publicationCheckpoint, cancellationToken).ConfigureAwait(false);


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
            return await PauseForNewFeedbackAsync(
                config, issueNumber, workingState, publicationCheckpoint, resultMarkdown,
                GitPublicationFailureClassifier.Explanation(reason), cancellationToken, reason).ConfigureAwait(false);
        }

        // LFS upload can take long enough for review feedback to arrive. Re-observe immediately
        // before pushing so a revision never publishes work produced without that feedback.
        var feedbackBeforePush = await CaptureFeedbackSnapshotAsync(config, config.Repository, mergeRequest.Number, cancellationToken).ConfigureAwait(false);
        if (!feedbackBeforePush.Versions.SetEquals(feedbackBeforeRevision.Versions))
        {
            return await PauseForNewFeedbackAsync(
                config,
                issueNumber,
                workingState,
                publicationCheckpoint,
                resultMarkdown,
                "New review feedback arrived during LFS upload. The retained worktree was preserved; review the feedback and request another revision.",
                cancellationToken).ConfigureAwait(false);
        }

        await deps.Git.FetchAsync(config.Repository.Id, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        var remoteHead = await deps.Git
            .TryResolveRemoteBranchCommitAsync(config.Repository.Id, workingState.Branch, cancellationToken)
            .ConfigureAwait(false);
        if (remoteHead is null)
        {
            return await PauseForNewFeedbackAsync(
                config, issueNumber, workingState, publicationCheckpoint, resultMarkdown,
                "Cannot publish revision: the remote agent branch is missing.", cancellationToken,
                WaitingReason.MissingRemoteRevisionBranch).ConfigureAwait(false);
        }
        if (!await deps.Git.IsAncestorAsync(config.Repository.Id, remoteHead, headCommit, cancellationToken).ConfigureAwait(false))
        {
            return await PauseForNewFeedbackAsync(
                config, issueNumber, workingState, publicationCheckpoint, resultMarkdown,
                "Cannot publish revision: the remote agent branch has unexpected history. Local and remote histories were preserved for human review.",
                cancellationToken, WaitingReason.RemoteHistoryRewrite).ConfigureAwait(false);
        }

        try
        {
            mergeRequest = await StoredMergeRequestIdentity.FindAsync(
                deps.Provider,
                config.Repository,
                workingContent.State.PullOrMergeRequest,
                workingState.Branch,
                workingState.TargetBranch,
                cancellationToken).ConfigureAwait(false)
                ?? throw new WorkflowContractException("Cannot publish revision: no merge request was found for this workflow's branch.");
        }
        catch (StoredMergeRequestIdentity.StoredMergeRequestUnavailableException exception)
        {
            return await PauseForNewFeedbackAsync(
                config, issueNumber, workingState, publicationCheckpoint, resultMarkdown,
                exception.Message, cancellationToken, WaitingReason.ManualIntervention).ConfigureAwait(false);
        }
        if (mergeRequest.IsMerged)
        {
            return await new CancellationWorkflow(deps)
                .CompleteOnMergeAsync(config, issueNumber, workingState, publicationCheckpoint, cancellationToken)
                .ConfigureAwait(false);
        }

        if (mergeRequest.IsClosed)
        {
            return await new CancellationWorkflow(deps)
                .CompleteOnCloseWithoutMergeAsync(config, issueNumber, workingState, publicationCheckpoint, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await deps.Git.PushAsync(config.Repository.Id, worktreePath, workingState.Branch, config.GitAuthentication, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException &&
            GitPublicationFailureClassifier.TryClassify(exception) is { } reason)
        {
            return await PauseForNewFeedbackAsync(
                config, issueNumber, workingState, publicationCheckpoint, resultMarkdown,
                GitPublicationFailureClassifier.Explanation(reason), cancellationToken, reason).ConfigureAwait(false);
        }

        var publishedState = workingState with
        {
            Phase = WorkflowPhase.Review,
            OperationalState = WorkflowOperationalState.Waiting,
            WaitingReason = WaitingReason.ReviewRequested,
            InterruptedPhase = null,
            ExpectedImplementationHead = headCommit,
            UpdatedAt = deps.Clock.UtcNow,
            // This is the actual end of the feedback observation used for the publication gate.
            // Advancing it to a later write time could silently skip feedback that arrives while
            // publishing the branch.
            ReviewFeedbackCutoff = feedbackBeforePush.Cutoff,
            ReviewFeedbackVersions = feedbackBeforePush.Versions,
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
            WorkflowCommand.Continue => WorkflowCommandLabels.Continue,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var labels = await deps.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        if (labels.Contains(commandLabel))
        {
            await deps.Provider.RemoveLabelAsync(workItem, commandLabel, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record FeedbackSnapshot(HashSet<string> Versions, DateTimeOffset Cutoff);

    private async Task<FeedbackSnapshot> CaptureFeedbackSnapshotAsync(
        WorkflowRepositoryConfig config,
        RepositoryRef repository,
        long mergeRequestNumber,
        CancellationToken cancellationToken)
    {
        // Record the observation boundary before enumerating provider pages. Feedback that arrives
        // after it cannot be advanced past by a concurrent publication checkpoint.
        var cutoff = deps.Clock.UtcNow;
        var versions = new HashSet<string>(StringComparer.Ordinal);
        string? authoritativeAuthor = null;
        await foreach (var comment in deps.Provider.GetMergeRequestCommentsAsync(repository, mergeRequestNumber, cancellationToken).ConfigureAwait(false))
        {
            var isAuthoritativeCanonicalComment = CanonicalCommentMarkdown.IsCanonicalComment(comment.Body) &&
                CanonicalCommentMarkdown.IsAuthoritativeCanonicalComment(
                    comment,
                    authoritativeAuthor ??= await CanonicalCommentLocator
                        .ResolveAuthoritativeIdentityAsync(deps.Provider, config.CanonicalCommentAuthor, cancellationToken)
                        .ConfigureAwait(false));
            if ((!config.IgnoreBotComments || !comment.IsBot) && !isAuthoritativeCanonicalComment)
            {
                versions.Add(CommentVersionEntry(comment.Id, comment.UpdatedAt, comment.Body));
            }
        }

        await foreach (var thread in deps.Provider.GetReviewThreadsAsync(repository, mergeRequestNumber, cancellationToken).ConfigureAwait(false))
        {
            versions.Add(ThreadResolutionEntry(thread.Id, thread.IsResolved));
            foreach (var comment in thread.Comments)
            {
                var isAuthoritativeCanonicalComment = CanonicalCommentMarkdown.IsCanonicalComment(comment.Body) &&
                    CanonicalCommentMarkdown.IsAuthoritativeCanonicalComment(
                        comment,
                        authoritativeAuthor ??= await CanonicalCommentLocator
                            .ResolveAuthoritativeIdentityAsync(deps.Provider, config.CanonicalCommentAuthor, cancellationToken)
                            .ConfigureAwait(false));
                if ((!config.IgnoreBotComments || !comment.IsBot) && !isAuthoritativeCanonicalComment)
                {
                    versions.Add(CommentVersionEntry(comment.Id, comment.UpdatedAt, comment.Body, thread.Id));
                }
            }
        }

        return new FeedbackSnapshot(versions, cutoff);
    }

    private async Task<bool> HasUnseenFeedbackSinceCheckpointAsync(
        WorkflowRepositoryConfig config,
        long mergeRequestNumber,
        DateTimeOffset cutoff,
        IReadOnlyCollection<string>? handledFeedbackVersions,
        CancellationToken cancellationToken)
    {
        var observedFeedbackVersions = handledFeedbackVersions is null
            ? null
            : new HashSet<string>(handledFeedbackVersions, StringComparer.Ordinal);

        var authoritativeAuthor = await CanonicalCommentLocator
            .ResolveAuthoritativeIdentityAsync(deps.Provider, config.CanonicalCommentAuthor, cancellationToken)
            .ConfigureAwait(false);

        await foreach (var comment in deps.Provider.GetMergeRequestCommentsAsync(
                           config.Repository,
                           mergeRequestNumber,
                           cancellationToken).ConfigureAwait(false))
        {
            if (IsHumanFeedback(config, comment, authoritativeAuthor) &&
                IsNewFeedbackSinceCheckpoint(
                    new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body, UpdatedAt: comment.UpdatedAt, CommentId: comment.Id),
                    cutoff,
                    observedFeedbackVersions))
            {
                return true;
            }
        }

        await foreach (var thread in deps.Provider.GetReviewThreadsAsync(
                           config.Repository,
                           mergeRequestNumber,
                           cancellationToken).ConfigureAwait(false))
        {
            if (observedFeedbackVersions is null || !observedFeedbackVersions.Contains(ThreadResolutionEntry(thread.Id, thread.IsResolved)))
            {
                return true;
            }

            foreach (var comment in thread.Comments)
            {
                if (IsHumanFeedback(config, comment, authoritativeAuthor) &&
                    IsNewFeedbackSinceCheckpoint(
                        new HumanComment(comment.AuthorLogin, comment.CreatedAt, comment.Body, thread.Id, thread.IsResolved, comment.UpdatedAt, comment.Id),
                        cutoff,
                        observedFeedbackVersions))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsHumanFeedback(
        WorkflowRepositoryConfig config,
        ProviderComment comment,
        string authoritativeAuthor) =>
        (!config.IgnoreBotComments || !comment.IsBot) &&
        (!CanonicalCommentMarkdown.IsCanonicalComment(comment.Body) ||
         !CanonicalCommentMarkdown.IsAuthoritativeCanonicalComment(comment, authoritativeAuthor));

    private static bool IsNewFeedbackSinceCheckpoint(
        HumanComment comment,
        DateTimeOffset cutoff,
        HashSet<string>? observedFeedbackVersions)
    {
        if ((comment.UpdatedAt ?? comment.CreatedAt) > cutoff)
        {
            return true;
        }

        if (observedFeedbackVersions is null)
        {
            // Legacy checkpoints stored only IDs. They cannot prove an edit was handled, so
            // conservatively surface feedback rather than suppressing it.
            return true;
        }

        if (comment.ThreadId is { } threadId &&
            !observedFeedbackVersions.Contains(ThreadResolutionEntry(threadId, comment.IsResolved)))
        {
            return true;
        }

        var version = comment.CommentId is { } commentId
            ? CommentVersionEntry(commentId, comment.UpdatedAt ?? comment.CreatedAt, comment.Body, comment.ThreadId)
            : null;
        return version is null || !observedFeedbackVersions.Contains(version);
    }

    private static string ThreadResolutionEntry(string threadId, bool isResolved) =>
        $"thread:{threadId}:resolved={isResolved}";

    private static string CommentVersionEntry(long commentId, DateTimeOffset updatedAt, string body, string? threadId = null)
    {
        var scope = threadId is null ? $"comment:{commentId}" : $"thread:{threadId}:{commentId}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return $"{scope}:{updatedAt:O}:{digest}";
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
