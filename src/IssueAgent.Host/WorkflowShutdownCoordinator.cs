namespace IssueAgent.Host;

public sealed record WorkflowShutdownResult(bool Drained, int CancelledSessions);

/// <summary>Applies the shutdown ordering after admission has stopped: drain active attempts for the
/// configured grace period, request bounded OMP cancellation only on expiry, then cancel workers.</summary>
public sealed class WorkflowShutdownCoordinator(
    WorkflowWorkerPool workerPool,
    ActiveOmpSessionRegistry activeOmpSessions)
{
    public async Task<WorkflowShutdownResult> DrainAndCancelAsync(
        TimeSpan gracePeriod,
        TimeSpan cancellationTimeout,
        CancellationTokenSource workerCancellation)
    {
        var drained = await workerPool
            .WaitForDrainAsync(gracePeriod, CancellationToken.None)
            .ConfigureAwait(false);
        var cancelledSessions = 0;
        if (!drained)
        {
            using var timeout = new CancellationTokenSource(cancellationTimeout);
            cancelledSessions = await activeOmpSessions.CancelAllAsync(timeout.Token).ConfigureAwait(false);
        }
        workerCancellation.Cancel();
        return new WorkflowShutdownResult(drained, cancelledSessions);
    }
}
