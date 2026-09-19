using System.Collections.Concurrent;
using IssueAgent.Configuration;
using IssueAgent.Domain;
using IssueAgent.Observability;
using Microsoft.Extensions.Options;

namespace IssueAgent.Host;

/// <summary>Owns admission, bounded execution, deferred explicit cancellation, and graceful drain
/// independently from provider polling.</summary>
public sealed partial class WorkflowWorkerPool(
    IOptions<IssueAgentOptions> options,
    ActiveOmpSessionRegistry activeOmpSessions,
    IssueAgentMetrics metrics,
    ILogger<WorkflowWorkerPool> logger) : IDisposable
{
    private readonly FairWorkAdmission admission = new();
    private readonly ConcurrentDictionary<WorkflowWorkKey, WorkflowCandidate> deferredCancellations = new();
    private readonly SemaphoreSlim available = new(0);
    private volatile bool accepting = true;

    public int QueuedCount => admission.QueuedCount;

    public int InFlightCount => admission.InFlightCount;

    public async Task AdmitAsync(IEnumerable<WorkflowCandidate> candidates, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.DiscoverySequence))
        {
            if (!accepting) break;
            if (!admission.TryEnqueue(candidate))
            {
                if (candidate.Command == WorkflowCommand.Cancel)
                {
                    deferredCancellations[candidate.Key] = candidate;
                    if (admission.IsInFlight(candidate.Key))
                    {
                        try
                        {
                            await activeOmpSessions.TryCancelAsync(candidate.Key, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            LogWorkflowFailure(logger, exception, candidate.Key.Provider, candidate.Key.RepositoryId, candidate.Key.IssueNumber);
                        }
                    }
                }
                continue;
            }
            available.Release();
        }
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var workers = Enumerable.Range(0, options.Value.Concurrency.Agent)
            .Select(_ => RunWorkerAsync(cancellationToken));
        return Task.WhenAll(workers);
    }

    public int StopAdmission()
    {
        accepting = false;
        var deferred = deferredCancellations.Count;
        deferredCancellations.Clear();
        return admission.DiscardQueued() + deferred;
    }

    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (admission.Count > 0)
        {
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!admission.TryStart(out var candidate)) continue;
            metrics.ActiveOperations.Add(1);
            using var activeOperation = metrics.BeginActiveOperation();
            try
            {
                await candidate!.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogWorkflowFailure(logger, exception, candidate!.Key.Provider, candidate.Key.RepositoryId, candidate.Key.IssueNumber);
            }
            finally
            {
                metrics.ActiveOperations.Add(-1);
                admission.Complete(candidate!.Key);
                if (accepting &&
                    deferredCancellations.TryRemove(candidate.Key, out var cancellation) &&
                    admission.TryEnqueue(cancellation))
                {
                    available.Release();
                }
            }
        }
    }

    public void Dispose() => available.Dispose();

    [LoggerMessage(EventId = 13, Level = LogLevel.Error,
        Message = "Workflow execution failed for issue {IssueNumber} in {Provider}/{Repository}")]
    private static partial void LogWorkflowFailure(
        ILogger logger,
        Exception exception,
        string provider,
        string repository,
        long issueNumber);
}
