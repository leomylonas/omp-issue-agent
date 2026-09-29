using System.Collections.Concurrent;
using IssueAgent.Configuration;
using IssueAgent.Domain;
using IssueAgent.Providers;
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
    private readonly ConcurrentDictionary<WorkflowWorkKey, WorkflowCandidate> deferredRateLimits = new();
    private readonly ConcurrentDictionary<WorkflowWorkKey, CancellationTokenSource> activeAttemptCancellations = new();
    private readonly CancellationTokenSource deferralCancellation = new();
    private readonly SemaphoreSlim available = new(0);

    private static readonly TimeSpan MaxDeferralDelay = TimeSpan.FromMinutes(5);
    private volatile bool accepting = true;

    public int QueuedCount => admission.QueuedCount;

    public int InFlightCount => admission.InFlightCount;

    public bool IsInFlight(WorkflowWorkKey key) => admission.IsInFlight(key);

    public bool IsAdmitted(WorkflowWorkKey key) => admission.IsAdmitted(key);

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
                    if (admission.TryReplaceQueued(candidate))
                    {
                        available.Release();
                        continue;
                    }

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
                        finally
                        {
                            CancelActiveAttempt(candidate.Key);
                        }
                    }
                    else if (deferredCancellations.TryRemove(candidate.Key, out var deferred) &&
                        admission.TryEnqueue(deferred))
                    {
                        available.Release();
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
        deferralCancellation.Cancel();
        var deferred = deferredCancellations.Count + deferredRateLimits.Count;
        deferredCancellations.Clear();
        deferredRateLimits.Clear();
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
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!admission.TryStart(
                out var candidate,
                key =>
                {
                    if (!activeAttemptCancellations.TryAdd(key, attemptCancellation))
                    {
                        throw new InvalidOperationException(
                            $"A workflow attempt is already active for {key.Provider}/{key.RepositoryId} issue {key.IssueNumber}.");
                    }
                }))
            {
                continue;
            }
            metrics.ActiveOperations.Add(1);
            using var activeOperation = metrics.BeginActiveOperation();
            try
            {
                using var rateLimitScheduling = PollingRateLimitScheduling.Enter();
                await candidate!.ExecuteAsync(attemptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
            {
                // An explicit cancel command interrupted this admitted attempt; its durable
                // cancellation candidate is queued in the finally block below.
            }
            catch (PollingRateLimitedException exception)
            {
                DeferRateLimitedCandidate(candidate!, exception.RetryAfter);
            }
            catch (Exception exception)
            {
                LogWorkflowFailure(logger, exception, candidate!.Key.Provider, candidate.Key.RepositoryId, candidate.Key.IssueNumber);
            }
            finally
            {
                activeAttemptCancellations.TryRemove(candidate!.Key, out _);
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


    private void CancelActiveAttempt(WorkflowWorkKey key)
    {
        if (activeAttemptCancellations.TryGetValue(key, out var attemptCancellation))
        {
            attemptCancellation.Cancel();
        }
    }

    private void DeferRateLimitedCandidate(WorkflowCandidate candidate, TimeSpan retryAfter)
    {
        if (!accepting)
        {
            return;
        }

        deferredRateLimits[candidate.Key] = candidate;
        _ = RequeueRateLimitedCandidateAsync(candidate.Key, retryAfter);
    }

    private async Task RequeueRateLimitedCandidateAsync(WorkflowWorkKey key, TimeSpan retryAfter)
    {
        try
        {
            await DelayForRequeueAsync(retryAfter, deferralCancellation.Token).ConfigureAwait(false);
            if (accepting &&
                deferredRateLimits.TryRemove(key, out var candidate) &&
                admission.TryEnqueue(candidate))
            {
                available.Release();
            }
        }
        catch (OperationCanceledException) when (deferralCancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task DelayForRequeueAsync(TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        var remaining = retryAfter <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : retryAfter;
        while (remaining > MaxDeferralDelay)
        {
            await Task.Delay(MaxDeferralDelay, cancellationToken).ConfigureAwait(false);
            remaining -= MaxDeferralDelay;
        }

        await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        deferralCancellation.Cancel();
        deferralCancellation.Dispose();
        available.Dispose();
    }

    [LoggerMessage(EventId = 13, Level = LogLevel.Error,
        Message = "Workflow execution failed for issue {IssueNumber} in {Provider}/{Repository}")]
    private static partial void LogWorkflowFailure(
        ILogger logger,
        Exception exception,
        string provider,
        string repository,
        long issueNumber);
}
