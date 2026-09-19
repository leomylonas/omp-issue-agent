using IssueAgent.Domain;
using IssueAgent.Workflow;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class WorkflowCommandRoutingTests
{
    [Fact]
    public void ResolvePausesForMergeRequestAmbiguityEvenWhenIssueHasCommand()
    {
        var issue = LabelProtocol.Analyze(
            [WorkflowLabels.PlannedPhase, WorkflowLabels.WaitingState, WorkflowCommandLabels.Replan]);
        var mergeRequest = LabelProtocol.Analyze(
            [WorkflowCommandLabels.Revise, WorkflowCommandLabels.Cancel]);

        var result = WorkflowCommandRouting.Resolve(issue, mergeRequest);

        Assert.True(result.IsAmbiguous);
        Assert.Null(result.Command);
    }

    [Fact]
    public void ContinueRouteAdoptsPublishedReviewWithoutStartingRevision()
    {
        var review = CreateState(WorkflowPhase.Review, WorkflowOperationalState.Waiting);

        var route = WorkflowCommandRouting.ContinueRoute(review, review);

        Assert.Null(route);
    }

    [Fact]
    public void ContinueRouteResumesOnlyDurablyInterruptedRevision()
    {
        var pausedRevision = CreateState(WorkflowPhase.Revising, WorkflowOperationalState.Waiting);
        var interruptedRevision = CreateState(WorkflowPhase.Revising, WorkflowOperationalState.Working);

        Assert.Null(WorkflowCommandRouting.ContinueRoute(pausedRevision, pausedRevision));
        Assert.Equal(WorkflowCommand.Revise, WorkflowCommandRouting.ContinueRoute(pausedRevision, interruptedRevision));
    }

    private static WorkflowState CreateState(WorkflowPhase phase, WorkflowOperationalState operationalState) => new(
        WorkflowId.New(),
        phase,
        operationalState,
        operationalState == WorkflowOperationalState.Waiting ? WaitingReason.ManualIntervention : null,
        PlanRevision: 1,
        ApprovedPlanRevision: 1,
        OmpSessionId: "session-1",
        Branch: "agent/issue-1",
        TargetBranch: "main",
        BaseCommit: "abc123",
        UpdatedAt: DateTimeOffset.UtcNow,
        PlanInputHash: "hash");
}
