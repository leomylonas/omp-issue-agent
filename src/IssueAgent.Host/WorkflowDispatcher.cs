using System.Collections;
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
            canonical = await CanonicalCommentLocator
                .FindAsync(provider, repository, issueNumber, cancellationToken)
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
            WorkflowWorkPriority.Reconciliation,
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
                await CanonicalCommentLocator.FindAsync(runtime.Provider, runtime.Repository, issueNumber, cancellationToken).ConfigureAwait(false) is not null)
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
        var workflowId = WorkflowId.New();
        var initialIssue = await runtime.Provider.GetIssueAsync(runtime.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        var initialTargetBranch = runtime.Config.TargetBranchOverride;
        var initialBaseCommit = await runtime.Dependencies.Git
            .ResolveBranchCommitAsync(runtime.Repository.Id, initialTargetBranch, cancellationToken)
            .ConfigureAwait(false);
        var initialBranchName = BranchNaming.DeriveBranchName(issueNumber, initialIssue.Title);
        var initialWorktreePath = Path.Combine(runtime.Config.WorkflowsStoragePath, workflowId.ToString(), "worktree");
        var worktreeCreated = false;
        IOmpClient omp = null!;
        try
        {
            await runtime.Dependencies.Git.CreateWorktreeAsync(
                runtime.Repository.Id,
                workflowId.ToString(),
                initialWorktreePath,
                initialBranchName,
                initialBaseCommit,
                cancellationToken).ConfigureAwait(false);
            worktreeCreated = true;
            omp = StartOmp(runtime, issueNumber, initialWorktreePath);
        }
        catch
        {
            if (worktreeCreated)
            {
                try
                {
                    await runtime.Dependencies.Git
                        .RemoveWorktreeAsync(runtime.Repository.Id, workflowId.ToString(), initialWorktreePath, CancellationToken.None)
                        .ConfigureAwait(false);
                    await runtime.Dependencies.Git
                        .RemoveLocalBranchAsync(runtime.Repository.Id, initialBranchName, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup; the original startup failure remains authoritative.
                }
            }
            throw;
        }

        await using var ompScope = omp;
        var stopwatch = Stopwatch.StartNew();
        metrics.PlanCount.Add(1, runtime.Tags);
        try
        {
            await new PlanningWorkflow(runtime.Dependencies)
                .RunInitialPlanningAsync(runtime.Config, issueNumber, omp, cancellationToken, workflowId)
                .ConfigureAwait(false);
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
                runtime.Provider, runtime.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
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
            var routedCommand = WorkflowCommandRouting.ContinueRoute(state, durableState);
            await RecoverContinueWorkspaceAsync(runtime.Dependencies, runtime.Config, state, cancellationToken).ConfigureAwait(false);
            if (routedCommand is null)
            {
                await ConsumeCommandAsync(
                    runtime.Provider, runtime.Repository, issueNumber, mergeRequest?.Number,
                    commandResolution.Sources, WorkflowCommand.Continue, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Material-deviation and new-input pauses carry the retained worktree/result as the
            // continuation provenance. Leave that durable pause intact until the owning workflow
            // has resumed it; otherwise a crash between this acknowledgement and RunAsync loses
            // the gate and can restart or discard retained work.
            var preservesRetainedContinuation = state.WaitingReason is
                WaitingReason.NewInputDuringImplementation or WaitingReason.MaterialPlanDeviation;
            if (!preservesRetainedContinuation)
            {
                await PersistContinueAcceptanceAsync(
                    runtime,
                    issueNumber,
                    canonical,
                    reconciled.Content!,
                    state,
                    routedCommand.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            await ConsumeCommandAsync(
                runtime.Provider, runtime.Repository, issueNumber, mergeRequest?.Number,
                commandResolution.Sources, WorkflowCommand.Continue, cancellationToken).ConfigureAwait(false);
            publishRetainedRevision = routedCommand == WorkflowCommand.Revise &&
                state.Phase == WorkflowPhase.Revising &&
                reconciled.Content!.ImplementationResult is { Length: > 0 };
            command = routedCommand;
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
            switch (command)
            {
                case WorkflowCommand.Replan:
                    metrics.PlanCount.Add(1, runtime.Tags);
                    await new PlanningWorkflow(runtime.Dependencies).RunReplanAsync(runtime.Config, issueNumber, state, omp, cancellationToken).ConfigureAwait(false);
                    break;
                case WorkflowCommand.Implement:
                    await new ImplementationWorkflow(runtime.Dependencies).RunAsync(runtime.Config, runtime.WorkflowMode, issueNumber, state, omp, cancellationToken).ConfigureAwait(false);
                    break;
                case WorkflowCommand.Revise:
                    await new RevisionWorkflow(runtime.Dependencies).RunAsync(
                        runtime.Config,
                        issueNumber,
                        state,
                        omp,
                        cancellationToken,
                        publishRetainedRevision).ConfigureAwait(false);
                    break;
            }
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
    internal static async Task RecoverContinueWorkspaceAsync(
        WorkflowDependencies dependencies,
        WorkflowRepositoryConfig config,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        var worktreePath = Path.Combine(config.WorkflowsStoragePath, state.WorkflowId.ToString(), "worktree");
        var remoteHead = await dependencies.Git
            .TryResolveRemoteBranchCommitAsync(config.Repository.Id, state.Branch, cancellationToken)
            .ConfigureAwait(false);
        if (!Directory.Exists(worktreePath))
        {
            await dependencies.Git.CreateWorktreeAsync(
                config.Repository.Id,
                state.WorkflowId.ToString(),
                worktreePath,
                state.Branch,
                remoteHead ?? state.BaseCommit,
                cancellationToken).ConfigureAwait(false);
        }
        else if (remoteHead is not null && !await dependencies.Git.HasUncommittedChangesAsync(
            config.Repository.Id, worktreePath, cancellationToken).ConfigureAwait(false))
        {
            await dependencies.Git.ResetWorktreeAsync(
                config.Repository.Id, worktreePath, remoteHead, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PersistContinueAcceptanceAsync(
        Runtime runtime,
        long issueNumber,
        ProviderComment canonicalComment,
        CanonicalCommentContent content,
        WorkflowState state,
        WorkflowCommand routedCommand,
        CancellationToken cancellationToken)
    {
        var acceptedState = state with
        {
            Phase = routedCommand switch
            {
                WorkflowCommand.Replan => WorkflowPhase.Planning,
                WorkflowCommand.Revise => WorkflowPhase.Revising,
                WorkflowCommand.Implement => WorkflowPhase.Implementing,
                _ => state.Phase,
            },
            OperationalState = WorkflowOperationalState.Working,
            WaitingReason = null,
            InterruptedPhase = null,
            UpdatedAt = runtime.Dependencies.Clock.UtcNow,
        };
        var updated = content with
        {
            State = CanonicalStateSerializer.ToDocument(acceptedState, content.State.PullOrMergeRequest),
        };
        await runtime.Provider.UpdateIssueCommentAsync(
            runtime.Repository,
            issueNumber,
            canonicalComment.Id,
            CanonicalCommentMarkdown.Render(updated),
            cancellationToken).ConfigureAwait(false);
    }

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
        var resolved = effectiveConfiguration.GetProvider(providerName).Repositories
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
        var contextOptions = new AgentContextBuilderOptions
        {
            IgnoreBotComments = resolved.IgnoreBotComments,
            RelatedIssueTraversalDepth = resolved.RelatedIssueTraversalDepth,
            AttachmentLimits = limits,
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
                gitIdentity),
            resolved.OmpRoles.GetValueOrDefault("planning", "plan"),
            resolved.OmpRoles.GetValueOrDefault("implementation", "task"),
            resolved.SupplementalInstructions,
            resolved.OmpTimeout,
            providers.TryGetGitAuthenticationForHost,
            resolved.CloseIssueOnMerge,
            resolved.OmpRoles.GetValueOrDefault("revision", "task"),
            resolved.OmpRoles.GetValueOrDefault("conflictResolution", "task"));
        var workflowMode = resolved.WorkflowMode == ConfiguredWorkflowMode.PlanOnly ? WorkflowMode.PlanOnly : WorkflowMode.Full;
        return new Runtime(provider, repository, dependencies, config, workflowMode, resolved.OmpTimeout, new TagList { { LogContextFields.Provider, providerName }, { LogContextFields.Repository, repository.Id } });
    }

    private IOmpClient StartOmp(Runtime runtime, long issueNumber, string workingDirectory)
    {
        var client = OmpProcessClientFactory.Start(
            runtime.Config.OmpExecutablePath,
            runtime.Config.OmpArguments,
            workingDirectory,
            runtime.Config.OmpAllowedEnvironment,
            options.Value.ShutdownGracePeriod);
        var observableClient = new ObservableOmpClient(client, metrics, ompLogger);
        return activeOmpSessions.Track(
            new WorkflowWorkKey(runtime.Provider.Name, runtime.Repository.Id, issueNumber),
            observableClient);
    }

    private static Dictionary<string, string?> ReadAmbientEnvironment() => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Where(entry => entry.Key is string).ToDictionary(entry => (string)entry.Key, entry => entry.Value as string, StringComparer.Ordinal);

    private sealed record Runtime(IGitProvider Provider, RepositoryRef Repository, WorkflowDependencies Dependencies, WorkflowRepositoryConfig Config, WorkflowMode WorkflowMode, TimeSpan? OmpTimeout, TagList Tags);


    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Canonical state is corrupt for {Provider}/{Repository} issue {IssueNumber}")]
    private static partial void LogCorruptState(ILogger logger, Exception exception, string provider, string repository, long issueNumber);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Persisted OMP session could not be resumed for {Provider}/{Repository} issue {IssueNumber}")]
    private static partial void LogOmpResumeFailure(ILogger logger, Exception exception, string provider, string repository, long issueNumber);
}
