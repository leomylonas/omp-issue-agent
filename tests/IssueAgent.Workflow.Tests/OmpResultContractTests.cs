using IssueAgent.Domain;

namespace IssueAgent.Workflow.Tests;

public sealed class OmpResultContractTests
{
    [Fact]
    public void PlanningPromptsRequireOutputOnlyExactJsonSchema()
    {
        var context = CreateContext();

        var initial = PlanningPromptBuilder.BuildInitialPlanPrompt(context);
        var replan = PlanningPromptBuilder.BuildReplanPrompt(context, []);

        AssertOutputOnlyPlanningContract(initial);
        AssertOutputOnlyPlanningContract(replan);
    }

    [Fact]
    public void ImplementationRevisionAndConflictPromptsRequireOutputOnlyExactJsonSchema()
    {
        var context = CreateContext();

        AssertOutputOnlyImplementationContract(ImplementationPromptBuilder.BuildImplementationPrompt(context));
        AssertOutputOnlyImplementationContract(ImplementationPromptBuilder.BuildRevisionPrompt(context, []));
        AssertOutputOnlyImplementationContract(ImplementationPromptBuilder.BuildConflictResolutionPrompt());
    }

    [Fact]
    public void PlanningResultParserRejectsMissingRequiredAndUnexpectedProperties()
    {
        Assert.Throws<WorkflowContractException>(() => PlanningResult.Parse("""{"planText":"Plan"}"""));
        Assert.Throws<WorkflowContractException>(() => PlanningResult.Parse("""{"planText":"Plan","decisions":[],"extra":true}"""));
        Assert.Throws<WorkflowContractException>(() => PlanningResult.Parse("""{"planText":null,"decisions":[]}"""));
        Assert.Throws<WorkflowContractException>(() => PlanningResult.Parse("""{"planText":"Plan","decisions":null}"""));
    }

    [Fact]
    public void ImplementationResultParserRejectsMissingRequiredAndUnexpectedProperties()
    {
        const string missingChecks = """{"summary":"Done","keyChanges":[],"decisions":[],"knownFailures":[],"deviations":[],"risks":[]}""";
        const string unexpectedProperty = """{"summary":"Done","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[],"extra":true}""";

        Assert.Throws<WorkflowContractException>(() => ImplementationResult.Parse(missingChecks));
        Assert.Throws<WorkflowContractException>(() => ImplementationResult.Parse(unexpectedProperty));
        Assert.Throws<WorkflowContractException>(() => ImplementationResult.Parse("""{"summary":null,"keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":[]}"""));
        Assert.Throws<WorkflowContractException>(() => ImplementationResult.Parse("""{"summary":"Done","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":[],"risks":null}"""));
    }

    private static void AssertOutputOnlyPlanningContract(string prompt)
    {
        Assert.Contains("Return only one JSON object", prompt, StringComparison.Ordinal);
        Assert.Contains("no Markdown fence, prose, or text before or after it", prompt, StringComparison.Ordinal);
        Assert.Contains("\"planText\":\"string\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"decisions\":[\"string\"]", prompt, StringComparison.Ordinal);
        Assert.Contains("use no other properties", prompt, StringComparison.Ordinal);
    }

    private static void AssertOutputOnlyImplementationContract(string prompt)
    {
        Assert.Contains("Return only one JSON object", prompt, StringComparison.Ordinal);
        Assert.Contains("no Markdown fence, prose, or text before or after it", prompt, StringComparison.Ordinal);
        Assert.Contains("\"summary\":\"string\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"keyChanges\":[\"string\"]", prompt, StringComparison.Ordinal);
        Assert.Contains("\"risks\":[\"string\"]", prompt, StringComparison.Ordinal);
        Assert.Contains("use no others", prompt, StringComparison.Ordinal);
    }

    private static AgentContext CreateContext()
    {
        var state = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval,
            1, null, "session-1", "agent/issue-1", "main", "abc123", DateTimeOffset.UtcNow);
        return new AgentContext(
            new IssueContext("github/octo/widgets", 1, "Title", "Description", [], [], []),
            [],
            new PlanContext(1, "Plan", []),
            null,
            state);
    }
}
