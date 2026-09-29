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
    public void PlanningPromptsRenderFullRelationshipPathForMultiHopRelatedIssues()
    {
        var context = CreateContext() with
        {
            RelatedIssues =
            [
                new RelatedIssueContext(
                    ["blocks", "blocked-by"],
                    new IssueContext("github/octo/widgets", 3, "Dependent issue", "Dependent description", [], [], [])),
            ],
        };

        var initial = PlanningPromptBuilder.BuildInitialPlanPrompt(context);
        var replan = PlanningPromptBuilder.BuildReplanPrompt(context, []);

        Assert.Contains("Related issue (blocks -> blocked-by, read-only context): Dependent issue", initial, StringComparison.Ordinal);
        Assert.Contains("Related issue (blocks -> blocked-by, read-only context): Dependent issue", replan, StringComparison.Ordinal);
    }


    [Fact]
    public void PlanningPromptsRenderIssueAttachmentProvenance()
    {
        var context = CreateContext();
        context = context with
        {
            PrimaryIssue = context.PrimaryIssue with
            {
                Attachments =
                [
                    new AttachmentReference(
                        "https://example.test/evidence.log",
                        "evidence.log",
                        "/tmp/evidence.log",
                        "issue-comment:1:7",
                        42),
                    new AttachmentReference(
                        "https://example.test/large.zip",
                        "large.zip",
                        string.Empty,
                        "issue-description:1",
                        0,
                        IsOmitted: true,
                        OmissionReason: "Attachment exceeds the size limit."),
                ],
            },
        };

        var prompt = PlanningPromptBuilder.BuildInitialPlanPrompt(context);

        Assert.Contains("AVAILABLE evidence.log (issue-comment:1:7): /tmp/evidence.log", prompt, StringComparison.Ordinal);
        Assert.Contains("OMITTED large.zip (issue-description:1): Attachment exceeds the size limit.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplanPromptIncludesLinkedReviewFeedbackAndAttachments()
    {
        var context = CreateContext() with
        {
            PullOrMergeRequest = new MergeRequestContext(
                42,
                "Published review",
                [new HumanComment("reviewer", DateTimeOffset.UtcNow, "Please preserve the public API.")],
                [new HumanComment("maintainer", DateTimeOffset.UtcNow, "Thread resolved.", "thread-1", true)],
                [
                    new AttachmentReference(
                        "https://example.test/review.log",
                        "review.log",
                        "/tmp/review.log",
                        "review-thread-comment:2:thread-1",
                        42),
                ]),
        };

        var prompt = PlanningPromptBuilder.BuildReplanPrompt(context, []);

        Assert.Contains("## Linked pull/merge request #42", prompt, StringComparison.Ordinal);
        Assert.Contains("Please preserve the public API.", prompt, StringComparison.Ordinal);
        Assert.Contains("Thread resolved.", prompt, StringComparison.Ordinal);
        Assert.Contains("AVAILABLE review.log (review-thread-comment:2:thread-1): /tmp/review.log", prompt, StringComparison.Ordinal);
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

    [Fact]
    public void ImplementationResultParserRequiresExplanationForMaterialDeviation()
    {
        const string materialDeviationWithoutExplanation =
            """{"summary":"Done","keyChanges":[],"decisions":[],"checksRun":[],"knownFailures":[],"deviations":["Requires a migration"],"risks":[],"isMaterialDeviation":true}""";

        Assert.Throws<WorkflowContractException>(() => ImplementationResult.Parse(materialDeviationWithoutExplanation));
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
