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
            return new WorkflowCandidateClassification(
                WorkflowCandidateKind.NewPlanning,
                WorkflowWorkPriority.NewPlanning,
                Command: null);
        }

        var labels = await provider
            .GetLabelsAsync(
                new ProviderWorkItemReference(repository, ProviderWorkItemKind.Issue, issueNumber),
                cancellationToken)
            .ConfigureAwait(false);
        var snapshot = LabelProtocol.Analyze(labels);
        if (!snapshot.IsAmbiguous && snapshot.Commands.Count == 1)
        {
            return new WorkflowCandidateClassification(
                WorkflowCandidateKind.ExistingWorkflow,
                WorkflowWorkPriority.HumanCommand,
                snapshot.Commands[0]);
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

    public async Task DispatchInitialPlanningAsync(string providerName, RepositoryOptions repositoryOptions, long issueNumber, CancellationToken cancellationToken)
    {
        var runtime = await PrepareRuntimeAsync(providerName, repositoryOptions, cancellationToken).ConfigureAwait(false);
        if (runtime is null) return;
        try
        {
            if (await CanonicalCommentLocator.FindAsync(runtime.Provider, runtime.Repository, issueNumber, cancellationToken).ConfigureAwait(false) is not null) return;
        }
        catch (CanonicalCommentCorruptException exception)
        {
            LogCorruptState(logger, exception, providerName, runtime.Repository.Id, issueNumber);
            return;
        }

        await using var omp = StartOmp(runtime, issueNumber);
        var stopwatch = Stopwatch.StartNew();
        metrics.PlanCount.Add(1, runtime.Tags);
        try
        {
            await new PlanningWorkflow(runtime.Dependencies).RunInitialPlanningAsync(runtime.Config, issueNumber, omp, cancellationToken).ConfigureAwait(false);
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
            canonical = await CanonicalCommentLocator.FindAsync(runtime.Provider, runtime.Repository, issueNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (CanonicalCommentCorruptException exception)
        {
            LogCorruptState(logger, exception, providerName, runtime.Repository.Id, issueNumber);
            return;
        }

        var labels = await runtime.Provider.GetLabelsAsync(
            new ProviderWorkItemReference(runtime.Repository, ProviderWorkItemKind.Issue, issueNumber),
            cancellationToken).ConfigureAwait(false);
        var snapshot = LabelProtocol.Analyze(labels);
        var command = !snapshot.IsAmbiguous && snapshot.Commands.Count == 1
            ? snapshot.Commands[0]
            : (WorkflowCommand?)null;
        var recoveryCommand = command is WorkflowCommand.Cancel or WorkflowCommand.Continue;
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
            return;
        }
        var canInspectReviewCommand = command is null && reconciled.State is not null;
        if (reconciled.Disposition != ReconciliationDisposition.ResumeAllowed &&
            !(recoveryCommand || canInspectReviewCommand))
        {
            return;
        }

        var state = reconciled.State!;
        if (command is null)
        {
            var mergeRequest = await runtime.Provider
                .FindMergeRequestAsync(runtime.Repository, state.Branch, state.TargetBranch, cancellationToken)
                .ConfigureAwait(false);
            if (mergeRequest is not null)
            {
                var mergeRequestLabels = await runtime.Provider.GetLabelsAsync(
                    new ProviderWorkItemReference(runtime.Repository, ProviderWorkItemKind.MergeRequest, mergeRequest.Number),
                    cancellationToken).ConfigureAwait(false);
                var mergeRequestSnapshot = LabelProtocol.Analyze(mergeRequestLabels);
                if (!mergeRequestSnapshot.IsAmbiguous && mergeRequestSnapshot.Commands.Count == 1)
                {
                    command = mergeRequestSnapshot.Commands[0];
                }
            }
        }
        if (command is null) return;
        if (command == WorkflowCommand.Cancel)
        {
            await new CancellationWorkflow(runtime.Dependencies)
                .RunAsync(runtime.Config, issueNumber, state, reconciled.Content!, omp: null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (command is not (WorkflowCommand.Implement or WorkflowCommand.Revise or WorkflowCommand.Continue or WorkflowCommand.Replan))
        {
            return;
        }

        await using var omp = StartOmp(runtime, issueNumber);
        try
        {
            await omp.ResumeSessionAsync(state.OmpSessionId, cancellationToken).ConfigureAwait(false);
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
                    await new RevisionWorkflow(runtime.Dependencies).RunAsync(runtime.Config, issueNumber, state, omp, cancellationToken).ConfigureAwait(false);
                    break;
                case WorkflowCommand.Continue:
                    await ContinueAsync(runtime, issueNumber, state, omp, cancellationToken).ConfigureAwait(false);
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

    private static async Task ContinueAsync(Runtime runtime, long issueNumber, WorkflowState state, IOmpClient omp, CancellationToken cancellationToken)
    {
        if (state.Phase is WorkflowPhase.Planning or WorkflowPhase.Planned)
        {
            await new PlanningWorkflow(runtime.Dependencies).RunReplanAsync(runtime.Config, issueNumber, state, omp, cancellationToken).ConfigureAwait(false);
            return;
        }
        await new ImplementationWorkflow(runtime.Dependencies).RunAsync(runtime.Config, runtime.WorkflowMode, issueNumber, state, omp, cancellationToken).ConfigureAwait(false);
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
        var config = new WorkflowRepositoryConfig(
            repository,
            Path.Combine(options.Value.Workspace.RootPath, "repos"),
            Path.Combine(options.Value.Workspace.RootPath, "workflows"),
            targetBranch,
            authentication,
            new GitIdentity(resolved.GitIdentityName, resolved.GitIdentityEmail),
            effectiveConfiguration.Omp.ExecutablePath,
            ["--rpc", "--session-dir", "/data/omp"],
            ompEnvironment.Create(
                ReadAmbientEnvironment(),
                new GitIdentity(resolved.GitIdentityName, resolved.GitIdentityEmail)),
            resolved.OmpRoles.GetValueOrDefault("planning", "plan"),
            resolved.OmpRoles.GetValueOrDefault("implementation", "task"),
            resolved.SupplementalInstructions,
            resolved.OmpTimeout,
            providers.TryGetGitAuthenticationForHost);
        var workflowMode = resolved.WorkflowMode == ConfiguredWorkflowMode.PlanOnly ? WorkflowMode.PlanOnly : WorkflowMode.Full;
        return new Runtime(provider, repository, dependencies, config, workflowMode, resolved.OmpTimeout, new TagList { { LogContextFields.Provider, providerName }, { LogContextFields.Repository, repository.Id } });
    }

    private IOmpClient StartOmp(Runtime runtime, long issueNumber)
    {
        var client = OmpProcessClientFactory.Start(
            runtime.Config.OmpExecutablePath,
            runtime.Config.OmpArguments,
            options.Value.Workspace.RootPath,
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
