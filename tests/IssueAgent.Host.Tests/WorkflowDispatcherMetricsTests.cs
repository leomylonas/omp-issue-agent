using System.Diagnostics;
using System.Diagnostics.Metrics;
using IssueAgent.Domain;
using IssueAgent.Observability;
using IssueAgent.Workflow;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class WorkflowDispatcherMetricsTests
{
    [Fact]
    public void RecordsOnlyDurableFailuresAgainstTheirWorkflowPhaseMetric()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var failedOutcome = new WorkflowOutcome(
            WorkflowOutcomeStatus.Failed,
            CreateState(WorkflowPhase.Failed) with { InterruptedPhase = WorkflowPhase.Planning });
        var waitingOutcome = new WorkflowOutcome(WorkflowOutcomeStatus.Waiting, CreateState(WorkflowPhase.Planned));

        WorkflowDispatcher.RecordDurableWorkflowFailure(waitingOutcome, metrics.PlanErrors, new TagList());
        WorkflowDispatcher.RecordDurableWorkflowFailure(failedOutcome, metrics.PlanErrors, new TagList());
        WorkflowDispatcher.RecordDurableWorkflowFailure(failedOutcome, metrics.ImplementationErrors, new TagList());

        Assert.Equal(1, capture.CountFor("issueagent.plans.errors"));
        Assert.Equal(1, capture.CountFor("issueagent.implementations.errors"));
    }

    private static WorkflowState CreateState(WorkflowPhase phase) => new(
        WorkflowId.New(),
        phase,
        WorkflowOperationalState.Waiting,
        WaitingReason.ManualIntervention,
        PlanRevision: 1,
        ApprovedPlanRevision: 1,
        OmpSessionId: "session-1",
        Branch: "agent/issue-1",
        TargetBranch: "main",
        BaseCommit: "abc123",
        UpdatedAt: DateTimeOffset.UtcNow,
        PlanInputHash: "hash");

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<(string Instrument, long Value)> measurements = [];

        public MetricCapture()
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == IssueAgentMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                lock (measurements)
                {
                    measurements.Add((instrument.Name, value));
                }
            });
            listener.Start();
        }

        public long CountFor(string instrumentName)
        {
            lock (measurements)
            {
                return measurements.Where(measurement => measurement.Instrument == instrumentName).Sum(measurement => measurement.Value);
            }
        }

        public void Dispose() => listener.Dispose();
    }
}
