using IssueAgent.Omp;

namespace IssueAgent.Workflow;

/// <summary>Drains an <see cref="IOmpClient.RunAsync"/> stream to its terminal event, exposing the
/// structured events observed along the way for logging and the final outcome for workflow logic.</summary>
public sealed record OmpRunOutcome(IReadOnlyList<OmpEvent> Events, OmpCompletedEvent? Completed, OmpErrorEvent? Error)
{
    public bool Succeeded => Completed is not null;
}

public static class OmpRunCollector
{
    public static async Task<OmpRunOutcome> RunToCompletionAsync(IOmpClient omp, OmpRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(omp);

        var events = new List<OmpEvent>();
        await foreach (var domainEvent in omp.RunAsync(request, cancellationToken).ConfigureAwait(false))
        {
            events.Add(domainEvent);
            if (domainEvent is OmpCompletedEvent completed)
            {
                return new OmpRunOutcome(events, completed, null);
            }

            if (domainEvent is OmpErrorEvent error)
            {
                return new OmpRunOutcome(events, null, error);
            }
        }

        throw new WorkflowContractException("OMP run ended without a completed or error event.");
    }
}
