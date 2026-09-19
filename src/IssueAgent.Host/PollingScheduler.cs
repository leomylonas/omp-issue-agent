using System.Collections.Concurrent;
using System.Diagnostics;
using IssueAgent.Configuration;
using System.Runtime.CompilerServices;
using IssueAgent.Observability;
using IssueAgent.Providers;
using Microsoft.Extensions.Options;

namespace IssueAgent.Host;

/// <summary>Discovers and classifies work independently from expensive workflow execution. Human
/// commands and new planning run on the bounded agent workers; reconciliation remains control work
/// and never consumes an OMP slot.</summary>
public sealed partial class PollingScheduler(
    IOptions<IssueAgentOptions> options,
    EffectiveIssueAgentConfiguration effectiveConfiguration,
    ProviderRegistry providers,
    WorkflowDispatcher dispatcher,
    WorkflowWorkerPool workerPool,
    IssueAgentMetrics metrics,
    ILogger<PollingScheduler> logger)
{
    private long discoverySequence;
    private long lastWorkspaceBytesMeasurementTicks;
    private static readonly TimeSpan WorkspaceBytesSampleInterval = TimeSpan.FromMinutes(5);

    public int QueuedCount => workerPool.QueuedCount;

    public int InFlightCount => workerPool.InFlightCount;

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        var workspaceBytes = MeasureWorkspaceBytesIfDue(configuration.Workspace.RootPath);
        if (workspaceBytes is { } measuredBytes)
        {
            metrics.UpdateWorkspaceBytes(measuredBytes);
        }
        var repositories = effectiveConfiguration.Providers
            .SelectMany(provider => provider.Repositories
                .Where(repository => repository.Enabled)
                .Select(repository => (Provider: provider, Repository: repository)))
            .ToArray();
        var discovered = new ConcurrentQueue<WorkflowCandidate>();
        using var pollingLimiter = new SemaphoreSlim(configuration.Concurrency.Polling);
        await Task.WhenAll(repositories.Select(item => DiscoverRepositoryAsync(
            item.Provider,
            item.Repository,
            pollingLimiter,
            discovered,
            cancellationToken))).ConfigureAwait(false);

        await workerPool.AdmitAsync(discovered, cancellationToken).ConfigureAwait(false);
    }

    public Task RunWorkersAsync(CancellationToken cancellationToken) =>
        workerPool.RunAsync(cancellationToken);

    public int StopAdmission() => workerPool.StopAdmission();

    public Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        workerPool.WaitForDrainAsync(timeout, cancellationToken);

    private async Task DiscoverRepositoryAsync(
        EffectiveProviderConfiguration providerOptions,
        EffectiveRepositoryConfiguration repositoryOptions,
        SemaphoreSlim pollingLimiter,
        ConcurrentQueue<WorkflowCandidate> discovered,
        CancellationToken cancellationToken)
    {
        await pollingLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var provider = providers.Get(providerOptions.Name);
        var repository = new RepositoryRef(
            repositoryOptions.Id,
            repositoryOptions.OwnerOrNamespace,
            repositoryOptions.Name);
        var tags = new TagList { { LogContextFields.Provider, provider.Name }, { LogContextFields.Repository, repository.Id } };
        metrics.PollCount.Add(1, tags);
        try
        {
            var identity = providerOptions.Source.IdentityOverride;
            if (string.IsNullOrWhiteSpace(identity))
            {
                identity = (await provider.GetCurrentIdentityAsync(cancellationToken).ConfigureAwait(false)).Login;
            }
            var startDate = repositoryOptions.StartDate;
            await foreach (var issue in DiscoverAllAsync(
                provider,
                repository,
                identity,
                startDate,
                cancellationToken).ConfigureAwait(false))
            {
                metrics.IssuesDiscovered.Add(1, tags);
                LogIssueDiscovered(logger, provider.Name, repository.Id, issue.Number);
                try
                {
                    var classification = await dispatcher
                        .ClassifyAsync(provider.Name, repositoryOptions.Source, issue.Number, cancellationToken)
                        .ConfigureAwait(false);
                    if (classification.Priority == WorkflowWorkPriority.Reconciliation)
                    {
                        var key = new WorkflowWorkKey(provider.Name, repository.Id, issue.Number);
                        if (workerPool.IsInFlight(key))
                        {
                            continue;
                        }

                        metrics.ActiveOperations.Add(1);
                        using var activeOperation = metrics.BeginActiveOperation();
                        try
                        {
                            await dispatcher.ExecuteAsync(
                                classification.Kind,
                                provider.Name,
                                repositoryOptions.Source,
                                issue.Number,
                                cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            metrics.ActiveOperations.Add(-1);
                        }

                        continue;
                    }

                    var sequence = Interlocked.Increment(ref discoverySequence);
                    discovered.Enqueue(new WorkflowCandidate(
                        new WorkflowWorkKey(provider.Name, repository.Id, issue.Number),
                        classification.Kind,
                        classification.Priority,
                        classification.Command,
                        sequence,
                        token => dispatcher.ExecuteAsync(
                            classification.Kind,
                            provider.Name,
                            repositoryOptions.Source,
                            issue.Number,
                            token)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogWorkflowFailure(logger, exception, provider.Name, repository.Id, issue.Number);
                }
            }
            metrics.MarkPollSucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            metrics.PollErrors.Add(1, tags);
            LogPollFailure(logger, exception, provider.Name, repository.Id);
        }
        finally
        {
            metrics.PollDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
            pollingLimiter.Release();
        }
    }

    private static async IAsyncEnumerable<IssueSummary> DiscoverAllAsync(
        IGitProvider provider,
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        await foreach (var issue in provider
            .DiscoverAssignedOpenIssuesAsync(repository, identity, startDate, cancellationToken)
            .ConfigureAwait(false))
        {
            if (seen.Add(issue.Number)) yield return issue;
        }
        await foreach (var issue in provider
            .DiscoverManagedIssuesAsync(repository, cancellationToken)
            .ConfigureAwait(false))
        {
            if (seen.Add(issue.Number)) yield return issue;
        }
    }


    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Discovered eligible issue {IssueNumber} in {Provider}/{Repository}")]
    private static partial void LogIssueDiscovered(ILogger logger, string provider, string repository, long issueNumber);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Polling failed for {Provider}/{Repository}")]
    private static partial void LogPollFailure(ILogger logger, Exception exception, string provider, string repository);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "Workflow dispatch failed for issue {IssueNumber} in {Provider}/{Repository}")]
    private static partial void LogWorkflowFailure(ILogger logger, Exception exception, string provider, string repository, long issueNumber);
    private long? MeasureWorkspaceBytesIfDue(string rootPath)
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var lastTicks = Interlocked.Read(ref lastWorkspaceBytesMeasurementTicks);
        var elapsed = Stopwatch.GetElapsedTime(lastTicks, nowTicks);
        if (lastTicks != 0 && elapsed < WorkspaceBytesSampleInterval)
        {
            return null;
        }

        if (Interlocked.CompareExchange(ref lastWorkspaceBytesMeasurementTicks, nowTicks, lastTicks) != lastTicks)
        {
            return null;
        }

        return MeasureWorkspaceBytes(rootPath);
    }

    private static long MeasureWorkspaceBytes(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

}
