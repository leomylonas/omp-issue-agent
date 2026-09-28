using System.Collections;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using IssueAgent.Configuration;
using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Observability;
using IssueAgent.Providers;
using IssueAgent.Workflow;
using Microsoft.Extensions.Options;

namespace IssueAgent.Host;

public sealed record WorkflowCandidateClassification(
    WorkflowCandidateKind Kind,
    WorkflowWorkPriority Priority,
    WorkflowCommand? Command);

public sealed partial class WorkflowDispatcher(
    IOptions<IssueAgentOptions> options,
    EffectiveIssueAgentConfiguration effectiveConfiguration,
    ProviderRegistry providers,
    IGitRepositoryManager git,
    OmpRuntimeEnvironmentFactory ompEnvironment,
    IWorkflowNotifier notifier,
    ActiveOmpSessionRegistry activeOmpSessions,
    DefaultBranchResolver defaultBranchResolver,
    IssueAgentMetrics metrics,
    ILogger<WorkflowDispatcher> logger,
    ILogger<ObservableOmpClient> ompLogger)
{
    public async ValueTask<WorkflowCandidateClassification> ClassifyAsync(
        string providerName,
        RepositoryOptions repositoryOptions,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var effectiveProvider = effectiveConfiguration.GetProvider(providerName);
        var resolved = effectiveProvider.Repositories.Single(repository => repository.Id == repositoryOptions.Id);
        var provider = providers.Get(providerName);
        var repository = new RepositoryRef(resolved.Id, resolved.OwnerOrNamespace, resolved.Name);
        ProviderComment? canonical;
        try
        {
            var canonicalCommentAuthor = await ResolveCanonicalCommentAuthorAsync(
                provider, effectiveProvider, cancellationToken).ConfigureAwait(false);
            canonical = await CanonicalCommentLocator
                .FindAsync(provider, repository, issueNumber, cancellationToken, canonicalCommentAuthor)
                .ConfigureAwait(false);
        }
        catch (CanonicalCommentCorruptException exception)
        {
            LogCorruptState(logger, exception, providerName, repository.Id, issueNumber);
            return new WorkflowCandidateClassification(WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.Reconciliation, null);
        }
        if (canonical is null)
        {
            var managedLabels = await provider.GetLabelsAsync(
                new ProviderWorkItemReference(repository, ProviderWorkItemKind.Issue, issueNumber),
                cancellationToken).ConfigureAwait(false);
            var managedSnapshot = LabelProtocol.Analyze(managedLabels);
            return new WorkflowCandidateClassification(
                managedSnapshot.Phase is not null || managedSnapshot.OperationalState is not null
                    ? WorkflowCandidateKind.ExistingWorkflow
                    : WorkflowCandidateKind.NewPlanning,
                managedSnapshot.Phase is not null || managedSnapshot.OperationalState is not null
                    ? WorkflowWorkPriority.Reconciliation
                    : WorkflowWorkPriority.NewPlanning,
                Command: null);
        }

        var labels = await provider
            .GetLabelsAsync(
                new ProviderWorkItemReference(repository, ProviderWorkItemKind.Issue, issueNumber),
                cancellationToken)
            .ConfigureAwait(false);
        var snapshot = LabelProtocol.Analyze(labels);
        var command = snapshot.SingleCommand;
        if (command is null)
        {
            try
            {
                var content = CanonicalCommentMarkdown.Parse(canonical.Body);
                var state = CanonicalStateSerializer.ToWorkflowState(content.State);
                var mergeRequest = await provider
                    .FindMergeRequestAsync(repository, state.Branch, state.TargetBranch, cancellationToken)
                    .ConfigureAwait(false);
                if (mergeRequest is not null)
                {
                    var mergeRequestLabels = await provider.GetLabelsAsync(
                        new ProviderWorkItemReference(repository, ProviderWorkItemKind.MergeRequest, mergeRequest.Number),
                        cancellationToken).ConfigureAwait(false);
                    command = LabelProtocol.Analyze(mergeRequestLabels).SingleCommand;
                }
            }
            catch (Exception exception) when (exception is CanonicalCommentCorruptException or CanonicalStateException)
            {
                // DispatchExistingAsync performs the durable corruption escalation.
            }
        }
        if (command is not null)
        {
            return new WorkflowCandidateClassification(
                WorkflowCandidateKind.ExistingWorkflow,
                WorkflowWorkPriority.HumanCommand,
                command);
        }

        return new WorkflowCandidateClassification(
            WorkflowCandidateKind.ExistingWorkflow,
            GetUncommandedExistingWorkflowPriority(canonical),
            Command: null);
    }

    public async Task ExecuteAsync(
        WorkflowCandidateKind kind,
        string providerName,
        RepositoryOptions repositoryOptions,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartDispatch(kind.ToString(), providerName, repositoryOptions.Id, issueNumber);
        if (kind == WorkflowCandidateKind.NewPlanning)
        {
            await DispatchInitialPlanningAsync(providerName, repositoryOptions, issueNumber, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DispatchExistingAsync(providerName, repositoryOptions, issueNumber, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DispatchInitialPlanningAsync(
        string providerName,
        RepositoryOptions repositoryOptions,
        long issueNumber,
        CancellationToken cancellationToken,
        bool replaceCorruptCanonical = false)
    {
        var runtime = await PrepareRuntimeAsync(providerName, repositoryOptions, cancellationToken).ConfigureAwait(false);
        if (runtime is null) return;
        if (replaceCorruptCanonical)
        {
            await LabelCatalog.EnsureAllAsync(runtime.Provider, runtime.Repository, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            if (!replaceCorruptCanonical &&
                await CanonicalCommentLocator.FindAsync(runtime.Provider, runtime.Repository, issueNumber, cancellationToken, runtime.Config.CanonicalCommentAuthor).ConfigureAwait(false) is not null)
            {
                return;
            }
        }
        catch (CanonicalCommentCorruptException exception)
        {
            LogCorruptState(logger, exception, providerName, runtime.Repository.Id, issueNumber);
            await runtime.Dependencies.Notifier.NotifyAsync(
                new WorkflowNotification(
                    WorkflowNotificationKind.HumanActionRequired,
                    runtime.Repository.Id,
                    issueNumber,
                    "unknown",
                    "IssueAgent found duplicate canonical comments; remove the ambiguity before continuing."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(runtime.Config.WorkflowsStoragePath);
        var planning = new PlanningWorkflow(runtime.Dependencies);
        var initialCheckpoint = await planning
            .CreateInitialCheckpointAsync(runtime.Config, issueNumber, cancellationToken)
            .ConfigureAwait(false);
        string? worktreePath = null;
        var bootstrapFailure = await ExecuteBootstrapWorktreeSetupAsync(
                async retryToken => worktreePath = await planning
                    .EnsureInitialPlanningWorktreeAsync(runtime.Config, initialCheckpoint, retryToken)
                    .ConfigureAwait(false),
                options.Value.Retry.ToPolicy(),
                cancellationToken)
            .ConfigureAwait(false);
        if (bootstrapFailure is not null)
        {
            var outcome = await planning
                .FailInitialPlanningBootstrapAsync(
                    runtime.Config,
                    issueNumber,
                    initialCheckpoint,
                    bootstrapFailure,
                    cancellationToken)
                .ConfigureAwait(false);
            RecordDurableWorkflowFailure(outcome, metrics.PlanErrors, runtime.Tags);
            return;
        }
        var retainedWorktreePath = worktreePath!;
        await using var omp = StartOmp(runtime, issueNumber, retainedWorktreePath);
        var stopwatch = Stopwatch.StartNew();
        metrics.PlanCount.Add(1, runtime.Tags);
        try
        {
            var outcome = await planning
                .RunInitialPlanningAsync(
                    runtime.Config,
                    issueNumber,
                    omp,
                    cancellationToken,
                    initialCheckpoint: initialCheckpoint,
                    initialWorktreePath: retainedWorktreePath)
                .ConfigureAwait(false);
            RecordDurableWorkflowFailure(outcome, metrics.PlanErrors, runtime.Tags);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            metrics.PlanErrors.Add(1, runtime.Tags);
            throw;
        }
        finally
        {
            metrics.PlanDuration.Record(stopwatch.Elapsed.TotalSeconds, runtime.Tags);
        }
    }

    public async Task DispatchExistingAsync(string providerName, RepositoryOptions repositoryOptions, long issueNumber, CancellationToken cancellationToken)
    {
        var runtime = await PrepareRuntimeAsync(providerName, repositoryOptions, cancellationToken).ConfigureAwait(false);
        if (runtime is null) return;
        ProviderComment? canonical;
        try
        {
            canonical = await CanonicalCommentLocator.FindAsync(
                runtime.Provider, runtime.Repository, issueNumber, cancellationToken, runtime.Config.CanonicalCommentAuthor).ConfigureAwait(false);
        }
        catch (CanonicalCommentCorruptException exception)
        {
            LogCorruptState(logger, exception, providerName, runtime.Repository.Id, issueNumber);
            await runtime.Dependencies.Notifier.NotifyAsync(
                new WorkflowNotification(
                    WorkflowNotificationKind.HumanActionRequired,
                    runtime.Repository.Id,
                    issueNumber,
                    "unknown",
                    "IssueAgent canonical state is corrupt or duplicated; repair the managed comment before continuing."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (canonical is null)
        {
            await PauseMissingCanonicalStateAsync(runtime, issueNumber, cancellationToken).ConfigureAwait(false);
            return;
        }


        var labels = await runtime.Provider.GetLabelsAsync(
            new ProviderWorkItemReference(runtime.Repository, ProviderWorkItemKind.Issue, issueNumber),
            cancellationToken).ConfigureAwait(false);
        var issueCommandSnapshot = LabelProtocol.Analyze(labels);
        var reconciliation = new WorkflowReconciliationService(runtime.Dependencies);
        using var reconciliationActivity = IssueAgentActivitySource.StartReconciliation(workflowId: null);
        var reconciled = await reconciliation
            .ReconcileAsync(runtime.Config, issueNumber, canonical!, cancellationToken)
            .ConfigureAwait(false);
        if (reconciled.State is not null)
        {
            var reconciledWorkflowId = reconciled.State.WorkflowId.Value.ToString();
            reconciliationActivity?.SetTag(LogContextFields.WorkflowId, reconciledWorkflowId);
            Activity.Current?.SetTag(LogContextFields.WorkflowId, reconciledWorkflowId);
        }
        if (reconciled.Disposition == ReconciliationDisposition.Corrupt)
        {
            LogCorruptState(
                logger,
                new CanonicalCommentCorruptException(reconciled.Explanation),
                providerName,
                runtime.Repository.Id,
                issueNumber);
            if (issueCommandSnapshot.SingleCommand == WorkflowCommand.Continue)
            {
                await ConsumeCommandAsync(
                    runtime.Provider,
                    runtime.Repository,
                    issueNumber,
                    mergeRequestNumber: null,
                    WorkflowCommandSource.Issue,
                    WorkflowCommand.Continue,
                    cancellationToken).ConfigureAwait(false);
                await DispatchInitialPlanningAsync(
                    providerName,
                    repositoryOptions,
                    issueNumber,
                    cancellationToken,
                    replaceCorruptCanonical: true).ConfigureAwait(false);
            }
            return;
        }
        if (reconciled.Disposition == ReconciliationDisposition.Completed)
        {
            return;
        }

        var state = reconciled.State!;
        ProviderMergeRequest? mergeRequest = null;
        LabelSnapshot? mergeRequestCommandSnapshot = null;
        mergeRequest = await runtime.Provider
            .FindMergeRequestAsync(runtime.Repository, state.Branch, state.TargetBranch, cancellationToken)
            .ConfigureAwait(false);
        if (mergeRequest is not null)
        {
            var mergeRequestLabels = await runtime.Provider.GetLabelsAsync(
                new ProviderWorkItemReference(runtime.Repository, ProviderWorkItemKind.MergeRequest, mergeRequest.Number),
                cancellationToken).ConfigureAwait(false);
            mergeRequestCommandSnapshot = LabelProtocol.Analyze(mergeRequestLabels);
        }

        var commandResolution = WorkflowCommandRouting.Resolve(issueCommandSnapshot, mergeRequestCommandSnapshot);
        if (commandResolution.IsAmbiguous)
        {
            await reconciliation.PauseForHumanAsync(
                runtime.Config,
                issueNumber,
                canonical,
                reconciled.Content!,
                state,
                WaitingReason.AmbiguousCommand,
                "Multiple conflicting workflow command labels are present on the issue or merge request.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var command = commandResolution.Command;
        var publishRetainedRevision = false;
        var initialPlanningBootstrap = IsInitialPlanningBootstrapCheckpoint(state) &&
            (command is null or WorkflowCommand.Continue);
        var recoveringFailedInitialPlanningBootstrap =
            TryGetFailedInitialPlanningBootstrapRecoveryCheckpoint(command, state, out var recoveredInitialCheckpoint);
        if (initialPlanningBootstrap || recoveringFailedInitialPlanningBootstrap)
        {
            if (!recoveringFailedInitialPlanningBootstrap &&
                command is null &&
                reconciled.Disposition != ReconciliationDisposition.ResumeAllowed)
            {
                return;
            }

            if (initialPlanningBootstrap && command == WorkflowCommand.Continue)
            {
                await ConsumeCommandAsync(
                    runtime.Provider, runtime.Repository, issueNumber, mergeRequest?.Number,
                    commandResolution.Sources, WorkflowCommand.Continue, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(runtime.Config.WorkflowsStoragePath);
            var planning = new PlanningWorkflow(runtime.Dependencies);
            var initialCheckpoint = recoveringFailedInitialPlanningBootstrap
                ? await planning.ResumeInitialPlanningBootstrapAsync(
                    runtime.Config, issueNumber, recoveredInitialCheckpoint, cancellationToken).ConfigureAwait(false)
                : state;
            string? worktreePath = null;
            var bootstrapFailure = await ExecuteBootstrapWorktreeSetupAsync(
                    async retryToken => worktreePath = await planning
                        .EnsureInitialPlanningWorktreeAsync(runtime.Config, initialCheckpoint, retryToken)
                        .ConfigureAwait(false),
                    options.Value.Retry.ToPolicy(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (bootstrapFailure is not null)
            {
                var failedBootstrapOutcome = await planning
                    .FailInitialPlanningBootstrapAsync(
                        runtime.Config,
                        issueNumber,
                        initialCheckpoint,
                        bootstrapFailure,
                        cancellationToken)
                    .ConfigureAwait(false);
                RecordDurableWorkflowFailure(failedBootstrapOutcome, metrics.PlanErrors, runtime.Tags);
                return;
            }
            var retainedWorktreePath = worktreePath!;
            await using var initialPlanningOmp = StartOmp(runtime, issueNumber, retainedWorktreePath);
            var outcome = await planning
                .RunInitialPlanningAsync(
                    runtime.Config,
                    issueNumber,
                    initialPlanningOmp,
                    cancellationToken,
                    initialCheckpoint: initialCheckpoint,
                    initialWorktreePath: retainedWorktreePath)
                .ConfigureAwait(false);
            RecordDurableWorkflowFailure(outcome, metrics.PlanErrors, runtime.Tags);
            return;
        }

        var recoveryCommand = command is WorkflowCommand.Cancel or WorkflowCommand.Continue;
        if (reconciled.Disposition != ReconciliationDisposition.ResumeAllowed && !recoveryCommand)
        {
            return;
        }
        if (command is null) return;
        if (command is not (WorkflowCommand.Cancel or WorkflowCommand.Implement or WorkflowCommand.Revise or WorkflowCommand.Continue or WorkflowCommand.Replan))
        {
            return;
        }
        if (command != WorkflowCommand.Continue && !WorkflowCommandRouting.IsPhaseCompatible(command.Value, state))
        {
            await reconciliation.PauseForHumanAsync(
                runtime.Config,
                issueNumber,
                canonical,
                reconciled.Content!,
                state,
                WaitingReason.ManualIntervention,
                $"The `{command.Value.ToString().ToLowerInvariant()}` command is not valid while the workflow is in the {state.Phase} phase.",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (command == WorkflowCommand.Cancel)
        {
            await new CancellationWorkflow(runtime.Dependencies)
                .RunAsync(runtime.Config, issueNumber, state, reconciled.Content!, omp: null, cancellationToken)
                .ConfigureAwait(false);
            if (commandResolution.Sources.HasFlag(WorkflowCommandSource.MergeRequest))
            {
                await ConsumeCommandAsync(
                    runtime.Provider,
                    runtime.Repository,
                    issueNumber,
                    mergeRequest?.Number,
                    WorkflowCommandSource.MergeRequest,
                    WorkflowCommand.Cancel,
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }


        if (command == WorkflowCommand.Continue)
        {
            var durableState = CanonicalStateSerializer.ToWorkflowState(reconciled.Content!.State);
            var rebuildRetainedRevisionFromRemoteHead =
                state.WaitingReason is WaitingReason.RemoteHistoryRewrite or WaitingReason.MissingRemoteRevisionBranch &&
                HasRetainedRevisionCheckpoint(state, reconciled.Content!);
            if (rebuildRetainedRevisionFromRemoteHead &&
                await runtime.Dependencies.Git
                    .TryResolveRemoteBranchCommitAsync(runtime.Repository.Id, state.Branch, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                // The prior rewrite blocker had an accepted remote tip. Its disappearance is a
                // distinct recovery blocker: preserve it durably so retained continue labels do
                // not rewrite canonical state or notify repeatedly while the branch is absent.
                await reconciliation.PauseForHumanAsync(
                    runtime.Config,
                    issueNumber,
                    canonical,
                    reconciled.Content!,
                    state,
                    WaitingReason.MissingRemoteRevisionBranch,
                    "The revision branch disappeared from the authoritative remote before the retained revision could be rebuilt. Restore the branch and continue again.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            var preservesRetainedContinuation = !rebuildRetainedRevisionFromRemoteHead &&
                (state.WaitingReason is
                    WaitingReason.NewInputDuringImplementation or WaitingReason.MaterialPlanDeviation or
                    WaitingReason.NewFeedbackDuringRevision ||
                 HasResultCheckpointedImplementation(state, reconciled.Content!) ||
                 HasRetainedRevisionCheckpoint(state, reconciled.Content!));
            var recoveredAuthoritativeRemote = true;
            string? acceptedRemoteHead = null;
            if (!preservesRetainedContinuation)
            {
                acceptedRemoteHead = await RecoverContinueWorkspaceFromRemoteHeadAsync(
                    runtime.Dependencies,
                    runtime.Config,
                    state,
                    reconciled.Content!,
                    cancellationToken).ConfigureAwait(false);
                recoveredAuthoritativeRemote = acceptedRemoteHead is not null;
            }
            if (!recoveredAuthoritativeRemote &&
                (state.Phase == WorkflowPhase.Review ||
                 state.WaitingReason is WaitingReason.RemoteHistoryRewrite or WaitingReason.MissingRemoteRevisionBranch))
            {
                var waitingReason = state.Phase == WorkflowPhase.Review
                    ? WaitingReason.ReviewRequested
                    : state.Phase == WorkflowPhase.Revising
                        ? WaitingReason.MissingRemoteRevisionBranch
                        : WaitingReason.RemoteHistoryRewrite;
                var message = state.Phase == WorkflowPhase.Review
                    ? "The review branch is not available on the authoritative remote. The continue command was retained and will be retried after the branch is restored."
                    : state.Phase == WorkflowPhase.Revising
                        ? "The revision branch disappeared from the authoritative remote while recovery was in progress. The retained revision was not rebuilt from the planned base; restore the branch and continue again."
                        : "The remote branch is not available to accept as authoritative. The continue command was retained; restore the branch and continue again.";
                await reconciliation.PauseForHumanAsync(
                    runtime.Config,
                    issueNumber,
                    canonical,
                    reconciled.Content!,
                    state,
                    waitingReason,
                    message,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!rebuildRetainedRevisionFromRemoteHead &&
                acceptedRemoteHead is not null &&
                state.WaitingReason is WaitingReason.RemoteHistoryRewrite or WaitingReason.MissingRemoteRevisionBranch)
            {
                state = await reconciliation.AcceptRemoteHistoryAsync(
                    runtime.Config,
                    issueNumber,
                    canonical,
                    reconciled.Content!,
                    state,
                    acceptedRemoteHead,
                    cancellationToken).ConfigureAwait(false);
                durableState = state;
            }
            var routedCommand = rebuildRetainedRevisionFromRemoteHead
                ? WorkflowCommand.Revise
                : WorkflowCommandRouting.ContinueRoute(state, durableState);
            if (routedCommand is null)
            {
                await ConsumeCommandAsync(
                    runtime.Provider, runtime.Repository, issueNumber, mergeRequest?.Number,
                    commandResolution.Sources, WorkflowCommand.Continue, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Keep the acknowledgement label until the routed workflow records its working
            // checkpoint and transitions phase/state labels. A process crash before that durable
            // hand-off then leaves the command available for safe replay.
            publishRetainedRevision = ShouldPublishRetainedRevision(state, reconciled.Content!, routedCommand);
            command = routedCommand;
        }

        if (ShouldRejectPlanOnlyImplementation(command.Value, runtime.WorkflowMode))
        {
            var outcome = await new ImplementationWorkflow(runtime.Dependencies)
                .RejectPlanOnlyAsync(runtime.Config, issueNumber, state, cancellationToken)
                .ConfigureAwait(false);
            RecordDurableWorkflowFailure(outcome, metrics.ImplementationErrors, runtime.Tags);
            return;
        }

        await using var omp = StartOmp(runtime, issueNumber, Path.Combine(runtime.Config.WorkflowsStoragePath, state.WorkflowId.ToString(), "worktree"));
        try
        {
            var sessionFile = state.OmpSessionFile
                ?? throw new InvalidOperationException("The workflow has no persisted OMP session file and cannot be resumed safely.");
            await omp.ResumeSessionAsync(state.OmpSessionId, sessionFile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await reconciliation.PauseForHumanAsync(
                runtime.Config,
                issueNumber,
                canonical!,
                reconciled.Content!,
                state,
                WaitingReason.ManualIntervention,
                $"The persisted OMP session could not be resumed ({exception.GetType().Name}). See operator logs for details.",
                cancellationToken).ConfigureAwait(false);
            LogOmpResumeFailure(logger, exception, providerName, runtime.Repository.Id, issueNumber);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var implementation = command is WorkflowCommand.Implement or WorkflowCommand.Revise or WorkflowCommand.Continue;
        if (implementation) metrics.ImplementationCount.Add(1, runtime.Tags);
        try
        {
            WorkflowOutcome outcome;
            switch (command)
            {
                case WorkflowCommand.Replan:
                    metrics.PlanCount.Add(1, runtime.Tags);
                    outcome = await new PlanningWorkflow(runtime.Dependencies)
                        .RunReplanAsync(runtime.Config, issueNumber, state, omp, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case WorkflowCommand.Implement:
                    outcome = await new ImplementationWorkflow(runtime.Dependencies)
                        .RunAsync(runtime.Config, runtime.WorkflowMode, issueNumber, state, omp, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case WorkflowCommand.Revise:
                    outcome = await new RevisionWorkflow(runtime.Dependencies)
                        .RunAsync(
                            runtime.Config,
                            issueNumber,
                            state,
                            omp,
                            cancellationToken,
                            publishRetainedRevision)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported workflow command '{command}'.");
            }
            RecordDurableWorkflowFailure(
                outcome,
                implementation ? metrics.ImplementationErrors : metrics.PlanErrors,
                runtime.Tags);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (implementation) metrics.ImplementationErrors.Add(1, runtime.Tags);
            else if (command == WorkflowCommand.Replan) metrics.PlanErrors.Add(1, runtime.Tags);
            throw;
        }
        finally
        {
            if (implementation) metrics.ImplementationDuration.Record(stopwatch.Elapsed.TotalSeconds, runtime.Tags);
            else if (command == WorkflowCommand.Replan) metrics.PlanDuration.Record(stopwatch.Elapsed.TotalSeconds, runtime.Tags);
        }
    }
    /// <summary>Restores a continuation worktree from the remote branch when it exists. The
    /// result tells recovery handling whether an authoritative branch tip was available; callers
    /// MUST NOT consume a Review or remote-history-rewrite Revision continue command without that evidence.</summary>
    internal static async Task<bool> RecoverContinueWorkspaceAsync(
        WorkflowDependencies dependencies,
        WorkflowRepositoryConfig config,
        WorkflowState state,
        CanonicalCommentContent content,
        CancellationToken cancellationToken) =>
        await RecoverContinueWorkspaceFromRemoteHeadAsync(dependencies, config, state, content, cancellationToken)
            .ConfigureAwait(false) is not null;

    private static async Task<string?> RecoverContinueWorkspaceFromRemoteHeadAsync(
        WorkflowDependencies dependencies,
        WorkflowRepositoryConfig config,
        WorkflowState state,
        CanonicalCommentContent content,
        CancellationToken cancellationToken)
    {
        var worktreePath = Path.Combine(config.WorkflowsStoragePath, state.WorkflowId.ToString(), "worktree");
        var remoteHead = await dependencies.Git
            .TryResolveRemoteBranchCommitAsync(config.Repository.Id, state.Branch, cancellationToken)
            .ConfigureAwait(false);
        var mustResetRetainedRevisionToRemoteHead =
            state.WaitingReason is WaitingReason.RemoteHistoryRewrite or WaitingReason.MissingRemoteRevisionBranch &&
            HasRetainedRevisionCheckpoint(state, content);
        if (state.Phase == WorkflowPhase.Revising &&
            state.WaitingReason is WaitingReason.RemoteHistoryRewrite or WaitingReason.MissingRemoteRevisionBranch &&
            remoteHead is null)
        {
            return null;
        }
        if (!Directory.Exists(worktreePath))
        {
            if (state.Phase == WorkflowPhase.Review && remoteHead is null)
            {
                return null;
            }
            await dependencies.Git.CreateWorktreeAsync(
                config.Repository.Id,
                state.WorkflowId.ToString(),
                worktreePath,
                state.Branch,
                remoteHead ?? state.BaseCommit,
                cancellationToken).ConfigureAwait(false);
        }
        else if (remoteHead is not null &&
                 (mustResetRetainedRevisionToRemoteHead ||
                  (!HasResultCheckpointedImplementation(state, content) &&
                   !HasRetainedRevisionCheckpoint(state, content) &&
                   state.WaitingReason is not (WaitingReason.NewInputDuringImplementation or
                       WaitingReason.MaterialPlanDeviation or WaitingReason.NewFeedbackDuringRevision) &&
                   !await dependencies.Git.HasUncommittedChangesAsync(
                       config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false))))
        {
            await dependencies.Git.ResetWorktreeAsync(
                config.Repository.Id, worktreePath, remoteHead, cancellationToken).ConfigureAwait(false);
        }

        return remoteHead;
    }

    private static bool HasResultCheckpointedImplementation(WorkflowState state, CanonicalCommentContent content) =>
        state.Phase == WorkflowPhase.Implementing &&
        state.PublicationStage == ImplementationPublicationStage.ResultCheckpointed &&
        state.ExpectedImplementationHead is { Length: > 0 } &&
        content.ImplementationResult is { Length: > 0 };

    internal static bool TryGetInitialPlanningCheckpoint(ProviderComment canonicalComment, out WorkflowState initialCheckpoint)
    {
        try
        {
            var state = CanonicalStateSerializer.ToWorkflowState(CanonicalCommentMarkdown.Parse(canonicalComment.Body).State);
            if (IsInitialPlanningBootstrapCheckpoint(state))
            {
                initialCheckpoint = state;
                return true;
            }
        }
        catch (Exception exception) when (exception is CanonicalCommentCorruptException or CanonicalStateException)
        {
            // Reconciliation performs the durable corruption escalation.
        }

        initialCheckpoint = null!;
        return false;
    }

    /// <summary>Runs the idempotent worktree creation step after its durable bootstrap checkpoint
    /// with the global bounded retry policy. Callers persist the exhausted failure before returning
    /// so polling cannot repeatedly treat the checkpoint as new planning work.</summary>
    internal static Task<Exception?> ExecuteBootstrapWorktreeSetupAsync(
        Func<CancellationToken, Task> createWorktree,
        RetryPolicy retryPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createWorktree);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        return retryPolicy.ExecuteAsync(createWorktree, cancellationToken);
    }
    // PollingScheduler executes reconciliation inline, but this recovery creates a new OMP
    // session and therefore must be admitted through the bounded agent worker pool.
    internal static WorkflowWorkPriority GetUncommandedExistingWorkflowPriority(ProviderComment canonicalComment) =>
        TryGetInitialPlanningCheckpoint(canonicalComment, out _)
            ? WorkflowWorkPriority.NewPlanning
            : WorkflowWorkPriority.Reconciliation;

    private static bool IsInitialPlanningBootstrapCheckpoint(WorkflowState state) =>
        state.Phase == WorkflowPhase.Planning &&
        state.OperationalState == WorkflowOperationalState.Working &&
        state.PlanRevision == 0 &&
        string.IsNullOrEmpty(state.OmpSessionId) &&
        state.OmpSessionFile is null;

    /// <summary>Recognizes a failed worktree-only planning bootstrap whose explicit continue
    /// command must create a new initial OMP session rather than resume a nonexistent one.</summary>
    internal static bool TryGetFailedInitialPlanningBootstrapRecoveryCheckpoint(
        WorkflowCommand? command,
        WorkflowState state,
        out WorkflowState initialCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (command == WorkflowCommand.Continue &&
            state.Phase == WorkflowPhase.Failed &&
            state.InterruptedPhase == WorkflowPhase.Planning &&
            state.PlanRevision == 0 &&
            string.IsNullOrEmpty(state.OmpSessionId) &&
            state.OmpSessionFile is null)
        {
            initialCheckpoint = state;
            return true;
        }

        initialCheckpoint = null!;
        return false;
    }

    internal static bool ShouldRejectPlanOnlyImplementation(WorkflowCommand command, WorkflowMode mode) =>
        command == WorkflowCommand.Implement && mode == WorkflowMode.PlanOnly;


    private static bool HasRetainedRevisionCheckpoint(WorkflowState state, CanonicalCommentContent content) =>
        state.Phase == WorkflowPhase.Revising &&
        content.ImplementationResult is { Length: > 0 };

    internal static void RecordDurableWorkflowFailure(
        WorkflowOutcome outcome,
        Counter<long> errorMetric,
        TagList tags)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(errorMetric);

        if (outcome.Status == WorkflowOutcomeStatus.Failed)
        {
            errorMetric.Add(1, tags);
        }
    }

    internal static bool ShouldPublishRetainedRevision(
        WorkflowState state,
        CanonicalCommentContent content,
        WorkflowCommand? routedCommand) =>
        routedCommand == WorkflowCommand.Revise &&
        state.WaitingReason is not (WaitingReason.NewFeedbackDuringRevision or WaitingReason.RemoteHistoryRewrite or
            WaitingReason.MissingRemoteRevisionBranch) &&
        HasRetainedRevisionCheckpoint(state, content);



    private static async Task ConsumeCommandAsync(
        IGitProvider provider,
        RepositoryRef repository,
        long issueNumber,
        long? mergeRequestNumber,
        WorkflowCommandSource sources,
        WorkflowCommand command,
        CancellationToken cancellationToken)
    {
        var commandLabel = command switch
        {
            WorkflowCommand.Continue => WorkflowCommandLabels.Continue,
            WorkflowCommand.Implement => WorkflowCommandLabels.Implement,
            WorkflowCommand.Revise => WorkflowCommandLabels.Revise,
            WorkflowCommand.Replan => WorkflowCommandLabels.Replan,
            WorkflowCommand.Cancel => WorkflowCommandLabels.Cancel,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        if (sources.HasFlag(WorkflowCommandSource.Issue))
        {
            await provider.RemoveLabelAsync(
                new ProviderWorkItemReference(repository, ProviderWorkItemKind.Issue, issueNumber),
                commandLabel,
                cancellationToken).ConfigureAwait(false);
        }
        if (sources.HasFlag(WorkflowCommandSource.MergeRequest) && mergeRequestNumber is not null)
        {
            await provider.RemoveLabelAsync(
                new ProviderWorkItemReference(repository, ProviderWorkItemKind.MergeRequest, mergeRequestNumber.Value),
                commandLabel,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PauseMissingCanonicalStateAsync(
        Runtime runtime,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var workItem = new ProviderWorkItemReference(runtime.Repository, ProviderWorkItemKind.Issue, issueNumber);
        var labels = await runtime.Provider.GetLabelsAsync(workItem, cancellationToken).ConfigureAwait(false);
        if (!labels.Contains(WorkflowLabels.WaitingState))
        {
            await runtime.Provider.EnsureLabelAsync(
                runtime.Repository,
                LabelCatalog.All.First(label => label.Name == WorkflowLabels.WaitingState),
                cancellationToken).ConfigureAwait(false);
            await runtime.Provider.AddLabelsAsync(workItem, [WorkflowLabels.WaitingState], cancellationToken).ConfigureAwait(false);
        }
        if (labels.Contains(WorkflowLabels.WorkingState))
        {
            await runtime.Provider.RemoveLabelAsync(workItem, WorkflowLabels.WorkingState, cancellationToken).ConfigureAwait(false);
        }
        await runtime.Dependencies.Notifier.NotifyAsync(
            new WorkflowNotification(
                WorkflowNotificationKind.HumanActionRequired,
                runtime.Repository.Id,
                issueNumber,
                "unknown",
                "IssueAgent canonical state is missing; restore the managed comment before continuing."),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Runtime?> PrepareRuntimeAsync(string providerName, RepositoryOptions repositoryOptions, CancellationToken cancellationToken)
    {
        var effectiveProvider = effectiveConfiguration.GetProvider(providerName);
        var resolved = effectiveProvider.Repositories
            .Single(repository => repository.Id == repositoryOptions.Id);
        var provider = providers.Get(providerName);
        var repository = new RepositoryRef(resolved.Id, resolved.OwnerOrNamespace, resolved.Name);
        var authentication = providers.GetGitAuthentication(repository.Id);
        await git.EnsureBareRepositoryAsync(repository.Id, resolved.CloneUrl, authentication, cancellationToken).ConfigureAwait(false);
        await git.FetchAsync(repository.Id, authentication, cancellationToken).ConfigureAwait(false);
        var targetBranch = await defaultBranchResolver
            .ResolveAsync(providerName, repository, resolved.TargetBranch, cancellationToken)
            .ConfigureAwait(false);
        var limits = new AttachmentLimits
        {
            MaxAttachmentSizeBytes = resolved.MaxAttachmentSizeBytes,
            MaxTotalSizeBytes = resolved.MaxTotalAttachmentSizeBytes,
        };
        var canonicalCommentAuthor = await ResolveCanonicalCommentAuthorAsync(
            provider, effectiveProvider, cancellationToken).ConfigureAwait(false);
        var contextOptions = new AgentContextBuilderOptions
        {
            IgnoreBotComments = resolved.IgnoreBotComments,
            RelatedIssueTraversalDepth = resolved.RelatedIssueTraversalDepth,
            AttachmentLimits = limits,
            CanonicalCommentAuthor = canonicalCommentAuthor,
            AllowedRepositories = effectiveProvider.Repositories
                .Where(repository => repository.Enabled)
                .Select(repository => new RepositoryRef(repository.Id, repository.OwnerOrNamespace, repository.Name))
                .ToArray(),
        };
        var context = new AgentContextBuilder(provider, new AttachmentPipeline(provider, limits), contextOptions);
        var dependencies = new WorkflowDependencies(provider, git, context, notifier, new SystemClock());
        var providerIdentity = resolved.UsesProviderIdentityForName || resolved.UsesProviderIdentityForEmail
            ? await provider.GetCurrentIdentityAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var gitName = resolved.UsesProviderIdentityForName
            ? providerIdentity!.DisplayName
            : resolved.GitIdentityName;
        var gitEmail = resolved.UsesProviderIdentityForEmail
            ? providerIdentity?.Email ?? throw new InvalidOperationException(
                $"Provider '{providerName}' did not report a commit email for the configured provider-derived Git identity.")
            : resolved.GitIdentityEmail;
        var gitIdentity = new GitIdentity(gitName, gitEmail);
        var config = new WorkflowRepositoryConfig(
            repository,
            Path.Combine(options.Value.Workspace.RootPath, "repos"),
            Path.Combine(options.Value.Workspace.RootPath, "workflows"),
            targetBranch,
            authentication,
            gitIdentity,
            effectiveConfiguration.Omp.ExecutablePath,
            ["--mode", "rpc", "--session-dir", Path.Combine(options.Value.Workspace.RootPath, "omp")],
            ompEnvironment.Create(
                ReadAmbientEnvironment(),
                gitIdentity,
                resolved),
            resolved.OmpRoles.GetValueOrDefault("planning", "plan"),
            resolved.OmpRoles.GetValueOrDefault("implementation", "task"),
            resolved.SupplementalInstructions,
            resolved.OmpTimeout,
            host => providers.GetSubmoduleGitAuthentication(repository.Id, host),
            resolved.CloseIssueOnMerge,
            resolved.OmpRoles.GetValueOrDefault("revision", "task"),
            resolved.OmpRoles.GetValueOrDefault("conflictResolution", "task"),
            resolved.IgnoreBotComments,
            canonicalCommentAuthor);
        var workflowMode = resolved.WorkflowMode == ConfiguredWorkflowMode.PlanOnly ? WorkflowMode.PlanOnly : WorkflowMode.Full;
        return new Runtime(provider, repository, dependencies, config, workflowMode, resolved.OmpTimeout, resolved.OmpExecutionSecrets.Values, new TagList { { LogContextFields.Provider, providerName }, { LogContextFields.Repository, repository.Id } });
    }

    internal static ValueTask<string> ResolveCanonicalCommentAuthorAsync(
        IGitProvider provider,
        EffectiveProviderConfiguration configuration,
        CancellationToken cancellationToken) =>
        CanonicalCommentLocator.ResolveAuthoritativeIdentityAsync(
            provider,
            CanonicalCommentIdentityOverride(configuration),
            cancellationToken);

    internal static string? CanonicalCommentIdentityOverride(EffectiveProviderConfiguration configuration) =>
        configuration.ApiToken is { Length: > 0 } ? null : configuration.Source.IdentityOverride;

    private IOmpClient StartOmp(Runtime runtime, long issueNumber, string workingDirectory)
    {
        var client = OmpProcessClientFactory.Start(
            runtime.Config.OmpExecutablePath,
            runtime.Config.OmpArguments,
            workingDirectory,
            runtime.Config.OmpAllowedEnvironment,
            options.Value.ShutdownGracePeriod,
            options.Value.Retry.ToPolicy(),
            runtime.OmpTimeout);
        var observableClient = new ObservableOmpClient(
            client,
            metrics,
            ompLogger,
            runtime.OmpExecutionSecrets);
        return activeOmpSessions.Track(
            new WorkflowWorkKey(runtime.Provider.Name, runtime.Repository.Id, issueNumber),
            observableClient);
    }

    private static Dictionary<string, string?> ReadAmbientEnvironment() => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Where(entry => entry.Key is string).ToDictionary(entry => (string)entry.Key, entry => entry.Value as string, StringComparer.Ordinal);

    private sealed record Runtime(
        IGitProvider Provider,
        RepositoryRef Repository,
        WorkflowDependencies Dependencies,
        WorkflowRepositoryConfig Config,
        WorkflowMode WorkflowMode,
        TimeSpan? OmpTimeout,
        IEnumerable<string> OmpExecutionSecrets,
        TagList Tags);


    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Canonical state is corrupt for {Provider}/{Repository} issue {IssueNumber}")]
    private static partial void LogCorruptState(ILogger logger, Exception exception, string provider, string repository, long issueNumber);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Persisted OMP session could not be resumed for {Provider}/{Repository} issue {IssueNumber}")]
    private static partial void LogOmpResumeFailure(ILogger logger, Exception exception, string provider, string repository, long issueNumber);
}
