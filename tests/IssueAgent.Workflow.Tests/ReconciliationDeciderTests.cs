using IssueAgent.Context;
using IssueAgent.Domain;

namespace IssueAgent.Workflow.Tests;

public sealed class ReconciliationDeciderTests
{
    [Fact]
    public void DecideWaitsWhenLabelsAreAmbiguous()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:planned", "agent:phase:review", "agent:state:waiting"]);
        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, RemoteCanonicalComment: null, false, null, null));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.AmbiguousCommand, decision.Reason);
    }

    [Fact]
    public void DecideWaitsWhenCanonicalCommentIsMissingOrCorrupt()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting"]);
        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, RemoteCanonicalComment: null, false, null, null));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.CorruptState, decision.Reason);
    }

    [Fact]
    public void DecideWaitsWhenLabelsAndCommentPhaseDisagree()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:review", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Planned, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, comment, false, null, null));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.CorruptState, decision.Reason);
    }

    [Fact]
    public void DecideRequiresPlanInputHashBeforeResuming()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Planned, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(
            new ReconciliationInput(labels, comment, true, "abc123", "abc123", PlanInputHashPresent: false));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.ReplanRequired, decision.Reason);
    }

    [Fact]
    public void DecideWaitsWhenPersistedStateIsWorking()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:implementing", "agent:state:working"]);
        var comment = CreateComment(WorkflowPhase.Implementing, WorkflowOperationalState.Working);

        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, comment, true, "abc123", "abc123"));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.ManualIntervention, decision.Reason);
    }
    [Fact]
    public void DecideAllowsFastForwardRemoteHumanCommit()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:implementing", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Implementing, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(
            new ReconciliationInput(labels, comment, true, "local-sha", "remote-sha", LocalHeadIsAncestorOfRemote: true));

        Assert.Equal(ReconciliationAction.ResumeAutomatically, decision.Action);
    }


    [Fact]
    public void DecideWaitsWhenLocalWorktreeHistoryDivergesFromRemoteBranch()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:implementing", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Implementing, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, comment, true, "local-sha", "remote-sha"));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.RemoteHistoryRewrite, decision.Reason);
    }

    [Fact]
    public void DecideWaitsWhenPublishedPhaseHasNoRemoteBranch()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:review", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Review, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, comment, true, "local-sha", null));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.RemoteHistoryRewrite, decision.Reason);
    }

    [Fact]
    public void DecideResumesAutomaticallyWhenStateIsConsistentAndWaiting()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Planned, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, comment, true, "abc123", "abc123"));

        Assert.Equal(ReconciliationAction.ResumeAutomatically, decision.Action);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void DecideWaitsWhenRetainedWorktreeIsMissing()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Planned, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, comment, false, null, "remote-sha"));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.ManualIntervention, decision.Reason);
    }

    [Fact]
    public void DecideWaitsAndPreservesDirtyRetainedWorktree()
    {
        var labels = LabelProtocol.Analyze(["agent:phase:planned", "agent:state:waiting"]);
        var comment = CreateComment(WorkflowPhase.Planned, WorkflowOperationalState.Waiting);

        var decision = ReconciliationDecider.Decide(new ReconciliationInput(labels, comment, true, "abc123", null, LocalWorktreeDirty: true));

        Assert.Equal(ReconciliationAction.WaitForHuman, decision.Action);
        Assert.Equal(WaitingReason.ManualIntervention, decision.Reason);
    }

    private static CanonicalCommentContent CreateComment(WorkflowPhase phase, WorkflowOperationalState operationalState)
    {
        var state = new WorkflowState(
            WorkflowId.New(), phase, operationalState, operationalState == WorkflowOperationalState.Waiting ? WaitingReason.PlanApproval : null,
            1, null, "session-1", "agent/issue-1", "main", "abc123", DateTimeOffset.UtcNow);
        var document = CanonicalStateSerializer.ToDocument(state, null);
        return new CanonicalCommentContent("Plan text.", [], null, document);
    }
}
