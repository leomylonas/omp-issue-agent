using IssueAgent.Domain;

namespace IssueAgent.Context.Tests;

public sealed class CanonicalStateSerializerTests
{
    [Fact]
    public void ToDocumentUsesKebabCaseForPhaseStateAndWaitingReason()
    {
        var state = CreateState(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.MaterialPlanDeviation);

        var document = CanonicalStateSerializer.ToDocument(state, "github/octo/widgets#9");

        Assert.Equal("planned", document.Phase);
        Assert.Equal("waiting", document.State);
        Assert.Equal("material-plan-deviation", document.WaitingReason);
        Assert.Equal("github/octo/widgets#9", document.PullOrMergeRequest);
    }

    [Fact]
    public void RoundTripThroughYamlPreservesWorkflowState()
    {
        var state = CreateState(WorkflowPhase.Review, WorkflowOperationalState.Waiting, WaitingReason.NewInputDuringImplementation) with
        {
            ImplementationInputDigest = "8911d6f83c8e5f8d14797f364ea3754e8404b3fc176122c94efcbe85613015c8",
            RebasedPublicationBase = "feedface",
        };
        var document = CanonicalStateSerializer.ToDocument(state, null);

        var yaml = CanonicalStateSerializer.Serialize(document);
        var roundTripped = CanonicalStateSerializer.Deserialize(yaml);
        var roundTrippedState = CanonicalStateSerializer.ToWorkflowState(roundTripped);

        Assert.Equal(state, roundTrippedState);
    }

    [Fact]
    public void RoundTripPreservesDistinctOmpSessionFile()
    {
        var state = CreateState(WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval)
            with { OmpSessionFile = "/data/omp/session-123.jsonl" };

        var roundTripped = CanonicalStateSerializer.ToWorkflowState(
            CanonicalStateSerializer.Deserialize(
                CanonicalStateSerializer.Serialize(CanonicalStateSerializer.ToDocument(state, null))));

        Assert.Equal("omp-session-123", roundTripped.OmpSessionId);
        Assert.Equal("/data/omp/session-123.jsonl", roundTripped.OmpSessionFile);
    }
    [Fact]
    public void ToWorkflowStateRejectsUnsupportedVersion()
    {
        var document = CanonicalStateSerializer.ToDocument(CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Working, null), null) with { Version = 2 };

        Assert.Throws<CanonicalStateException>(() => CanonicalStateSerializer.ToWorkflowState(document));
    }

    [Fact]
    public void DeserializeRejectsMalformedYaml()
    {
        Assert.Throws<CanonicalStateException>(() => CanonicalStateSerializer.Deserialize("not: [valid: yaml"));
    }

    [Fact]
    public void DeserializeRejectsUnrecognizedPhaseValue()
    {
        var document = CanonicalStateSerializer.ToDocument(CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Working, null), null);
        var yaml = CanonicalStateSerializer.Serialize(document).Replace("phase: planning", "phase: bogus-phase", StringComparison.Ordinal);
        var parsed = CanonicalStateSerializer.Deserialize(yaml);

        Assert.Throws<CanonicalStateException>(() => CanonicalStateSerializer.ToWorkflowState(parsed));
    }

    [Theory]
    [InlineData("phase: planning", "phase: 99")]
    [InlineData("state: working", "state: 99")]
    [InlineData("waitingReason: plan-approval", "waitingReason: 99")]
    public void ToWorkflowStateRejectsUndefinedNumericCanonicalEnums(string expected, string replacement)
    {
        var document = CanonicalStateSerializer.ToDocument(
            CreateState(WorkflowPhase.Planning, WorkflowOperationalState.Working, WaitingReason.PlanApproval),
            null);
        var yaml = CanonicalStateSerializer.Serialize(document).Replace(expected, replacement, StringComparison.Ordinal);
        var parsed = CanonicalStateSerializer.Deserialize(yaml);

        Assert.Throws<CanonicalStateException>(() => CanonicalStateSerializer.ToWorkflowState(parsed));
    }

    private static WorkflowState CreateState(WorkflowPhase phase, WorkflowOperationalState operationalState, WaitingReason? waitingReason) => new(
        WorkflowId.New(),
        phase,
        operationalState,
        waitingReason,
        PlanRevision: 2,
        ApprovedPlanRevision: operationalState == WorkflowOperationalState.Working ? 2 : null,
        OmpSessionId: "omp-session-123",
        Branch: "agent/issue-9-fix",
        TargetBranch: "main",
        BaseCommit: "deadbeefcafebabe",
        UpdatedAt: DateTimeOffset.Parse("2024-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
}
