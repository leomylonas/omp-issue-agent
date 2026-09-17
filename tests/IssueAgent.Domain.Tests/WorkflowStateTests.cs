using IssueAgent.Domain;

namespace IssueAgent.Domain.Tests;

public sealed class WorkflowStateTests
{
    [Fact]
    public void EnsureValidRequiresWaitingReasonWhenWaiting()
    {
        var state = CreateState(WorkflowOperationalState.Waiting, waitingReason: null);

        var exception = Assert.Throws<InvalidOperationException>(state.EnsureValid);

        Assert.Equal("Waiting workflows must record a waiting reason.", exception.Message);
    }

    [Fact]
    public void EnsureValidRejectsApprovedRevisionAfterCurrentPlan()
    {
        var state = CreateState(WorkflowOperationalState.Working, waitingReason: null) with
        {
            PlanRevision = 2,
            ApprovedPlanRevision = 3,
        };

        var exception = Assert.Throws<InvalidOperationException>(state.EnsureValid);

        Assert.Equal("Approved plan revision cannot exceed the current plan revision.", exception.Message);
    }

    [Fact]
    public void CommandLabelsParseOnlyCanonicalValues()
    {
        var parsed = WorkflowCommandLabels.TryParse("agent:cmd:implement", out var command);
        var unparsed = WorkflowCommandLabels.TryParse("agent:cmd:approve", out _);

        Assert.True(parsed);
        Assert.Equal(WorkflowCommand.Implement, command);
        Assert.False(unparsed);
    }

    private static WorkflowState CreateState(WorkflowOperationalState operationalState, WaitingReason? waitingReason) => new(
        WorkflowId.New(),
        WorkflowPhase.Planned,
        operationalState,
        waitingReason,
        1,
        1,
        "omp-session",
        "agent/issue-1-example",
        "main",
        "deadbeef",
        DateTimeOffset.UtcNow);
}
