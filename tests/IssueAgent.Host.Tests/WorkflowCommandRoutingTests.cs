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
    public void ResolveIgnoresImplementAndReplanLabelsOnMergeRequests()
    {
        var issue = LabelProtocol.Analyze([]);

        foreach (var unsupportedCommand in new[] { WorkflowCommandLabels.Implement, WorkflowCommandLabels.Replan })
        {
            var result = WorkflowCommandRouting.Resolve(issue, LabelProtocol.Analyze([unsupportedCommand]));

            Assert.False(result.IsAmbiguous);
            Assert.Null(result.Command);
            Assert.Equal(WorkflowCommandSource.None, result.Sources);
        }
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

    [Fact]
    public void ContinueRouteResumesInterruptedPlanningAsReplan()
    {
        var pausedPlanning = CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Waiting);
        var interruptedPlanning = CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Working);

        Assert.Null(WorkflowCommandRouting.ContinueRoute(pausedPlanning, pausedPlanning));
        Assert.Equal(WorkflowCommand.Replan, WorkflowCommandRouting.ContinueRoute(pausedPlanning, interruptedPlanning));
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
