using IssueAgent.Domain;

namespace IssueAgent.Workflow.Tests;

public sealed class LabelProtocolTests
{
    [Fact]
    public void AnalyzeParsesSinglePhaseStateAndCommand()
    {
        var snapshot = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting", "agent:cmd:implement", "bug"]);

        Assert.Equal(WorkflowPhase.Planned, snapshot.Phase);
        Assert.Equal(WorkflowOperationalState.Waiting, snapshot.OperationalState);
        Assert.Equal([WorkflowCommand.Implement], snapshot.Commands);
        Assert.False(snapshot.IsAmbiguous);
    }

    [Fact]
    public void AnalyzeFlagsMissingPhaseLabel()
    {
        var snapshot = LabelProtocol.Analyze(["agent:state:waiting"]);

        Assert.Equal(LabelAmbiguity.MissingPhaseLabel, snapshot.Ambiguity);
    }

    [Fact]
    public void AnalyzeFlagsMultiplePhaseLabels()
    {
        var snapshot = LabelProtocol.Analyze(["agent:phase:planned", "agent:phase:review", "agent:state:waiting"]);

        Assert.Equal(LabelAmbiguity.MultiplePhaseLabels, snapshot.Ambiguity);
    }

    [Fact]
    public void AnalyzeFlagsMissingStateLabel()
    {
        var snapshot = LabelProtocol.Analyze(["agent:phase:planned"]);

        Assert.Equal(LabelAmbiguity.MissingStateLabel, snapshot.Ambiguity);
    }

    [Fact]
    public void AnalyzeFlagsMultipleStateLabels()
    {
        var snapshot = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting", "agent:state:working"]);

        Assert.Equal(LabelAmbiguity.MultipleStateLabels, snapshot.Ambiguity);
    }

    [Fact]
    public void AnalyzeFlagsMultipleConflictingCommandLabels()
    {
        var snapshot = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting", "agent:cmd:implement", "agent:cmd:cancel"]);

        Assert.Equal(LabelAmbiguity.MultipleCommandLabels, snapshot.Ambiguity);
    }


    [Fact]
    public void SingleCommandIsAvailableEvenWhenPhaseLabelsAreMissing()
    {
        var snapshot = LabelProtocol.Analyze(["agent:cmd:revise"]);

        Assert.Equal(WorkflowCommand.Revise, snapshot.SingleCommand);
        Assert.True(snapshot.IsPhaseStateAmbiguous);
    }
    [Fact]
    public void ComputeTransitionSwapsPhaseAndStateLabelsAndPreservesOtherLabels()
    {
        var current = new[] { "agent:phase:planning", "agent:state:working", "bug", "priority:high" };

        var (toAdd, toRemove) = LabelProtocol.ComputeTransition(current, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, []);

        Assert.Equal(["agent:phase:planned", "agent:state:waiting"], toAdd);
        Assert.Equal(["agent:phase:planning", "agent:state:working"], toRemove);
    }

    [Fact]
    public void ComputeTransitionConsumesAcceptedCommandLabels()
    {
        var current = new[] { "agent:phase:planned", "agent:state:waiting", "agent:cmd:replan" };

        var (toAdd, toRemove) = LabelProtocol.ComputeTransition(current, WorkflowPhase.Planning, WorkflowOperationalState.Working, [WorkflowCommand.Replan]);

        Assert.Contains("agent:cmd:replan", toRemove);
        Assert.DoesNotContain("agent:cmd:replan", toAdd);
    }

    [Fact]
    public void ComputeTransitionIsNoOpWhenAlreadyAtTargetState()
    {
        var current = new[] { "agent:phase:planned", "agent:state:waiting" };

        var (toAdd, toRemove) = LabelProtocol.ComputeTransition(current, WorkflowPhase.Planned, WorkflowOperationalState.Waiting, []);

        Assert.Empty(toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void ComputeTerminalTransitionRemovesOperationalLabelsAndConsumesCommand()
    {
        var current = new[] { "agent:phase:implementing", "agent:state:working", "agent:cmd:cancel", "bug" };

        var (toAdd, toRemove) = LabelProtocol.ComputeTerminalTransition(
            current,
            WorkflowPhase.Cancelled,
            [WorkflowCommand.Cancel]);

        Assert.Equal(["agent:phase:cancelled"], toAdd);
        Assert.Equal(["agent:phase:implementing", "agent:state:working", "agent:cmd:cancel"], toRemove);
    }

    [Fact]
    public void LabelCatalogIncludesEveryHumanCommand()
    {
        var labels = LabelCatalog.All.Select(label => label.Name);

        Assert.Contains(WorkflowCommandLabels.Replan, labels);
        Assert.Contains(WorkflowCommandLabels.Implement, labels);
        Assert.Contains(WorkflowCommandLabels.Revise, labels);
        Assert.Contains(WorkflowCommandLabels.Continue, labels);
        Assert.Contains(WorkflowCommandLabels.Cancel, labels);
    }
}
