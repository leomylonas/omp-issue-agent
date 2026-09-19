using IssueAgent.Domain;

namespace IssueAgent.Context.Tests;

public sealed class CanonicalCommentMarkdownTests
{
    [Fact]
    public void RenderThenParseRoundTripsPlanDecisionsAndState()
    {
        var content = CreateContent(
            planText: "Investigate the null-reference and add a guard clause.",
            decisions: ["Use a guard clause instead of a try/catch for clarity and performance."],
            implementationResult: null);

        var rendered = CanonicalCommentMarkdown.Render(content);
        var parsed = CanonicalCommentMarkdown.Parse(rendered);

        Assert.Equal(content.PlanText, parsed.PlanText);
        Assert.Equal(content.DecisionsAndRationale, parsed.DecisionsAndRationale);
        Assert.Null(parsed.ImplementationResult);
        Assert.Equal(content.State, parsed.State);
    }

    [Fact]
    public void RenderThenParseRoundTripsMultilineDecisionRationale()
    {
        var content = CreateContent(
            planText: "Plan text",
            decisions: [
                "First line of rationale.\nSecond line explains the tradeoff.\nThird line has the final detail.",
                "A second, single-line decision.",
            ],
            implementationResult: null);

        var rendered = CanonicalCommentMarkdown.Render(content);
        var parsed = CanonicalCommentMarkdown.Parse(rendered);

        Assert.Equal(content.DecisionsAndRationale, parsed.DecisionsAndRationale);
    }

    [Fact]
    public void RenderThenParseRoundTripsWhenDecisionsContainBlankEntries()
    {
        var content = CreateContent(
            planText: "Plan text",
            decisions: ["First.", "", "   ", "Third."],
            implementationResult: null);

        var rendered = CanonicalCommentMarkdown.Render(content);
        var parsed = CanonicalCommentMarkdown.Parse(rendered);

        Assert.Equal(["First.", "Third."], parsed.DecisionsAndRationale);
    }

    [Fact]
    public void RenderIncludesHeaderAndLocatorMarker()
    {
        var rendered = CanonicalCommentMarkdown.Render(CreateContent("Plan text", [], null));

        Assert.Contains(CanonicalCommentMarkdown.Header, rendered, StringComparison.Ordinal);
        Assert.Contains(CanonicalCommentMarkdown.StateLocatorMarker, rendered, StringComparison.Ordinal);
        Assert.True(CanonicalCommentMarkdown.IsCanonicalComment(rendered));
    }

    [Fact]
    public void RenderThenParseRoundTripsImplementationResult()
    {
        var content = CreateContent(
            planText: "Plan text",
            decisions: ["Decision one", "Decision two"],
            implementationResult: "Implemented the fix and added a regression test.\n\nAll checks passed.");

        var parsed = CanonicalCommentMarkdown.Parse(CanonicalCommentMarkdown.Render(content));

        Assert.Equal(content.ImplementationResult, parsed.ImplementationResult);
        Assert.Equal(content.DecisionsAndRationale, parsed.DecisionsAndRationale);
    }

    [Fact]
    public void IsCanonicalCommentReturnsFalseForOrdinaryHumanComment()
    {
        Assert.False(CanonicalCommentMarkdown.IsCanonicalComment("Looks good to me, thanks!"));
    }

    [Fact]
    public void ParseThrowsCorruptExceptionWhenLocatorMarkerMissing()
    {
        Assert.Throws<CanonicalCommentCorruptException>(() => CanonicalCommentMarkdown.Parse("Some random text without the marker."));
    }

    [Fact]
    public void ParseThrowsCorruptExceptionWhenYamlFenceMissing()
    {
        var body = CanonicalCommentMarkdown.Header + "\n\n## Implementation plan\nplan\n\n" + CanonicalCommentMarkdown.StateLocatorMarker;

        Assert.Throws<CanonicalCommentCorruptException>(() => CanonicalCommentMarkdown.Parse(body));
    }

    [Fact]
    public void ParseThrowsCorruptExceptionWhenStateYamlIsMalformed()
    {
        var body = CanonicalCommentMarkdown.Header + "\n\n## Implementation plan\nplan\n\n```yaml\nnot: [valid\n```\n\n" + CanonicalCommentMarkdown.StateLocatorMarker;

        Assert.Throws<CanonicalCommentCorruptException>(() => CanonicalCommentMarkdown.Parse(body));
    }

    [Fact]
    public void ParseUsesGeneratedStateDetailsInsteadOfYamlExampleInPlan()
    {
        var content = CreateContent(
            "Use this example while evaluating the parser:\n\n```yaml\nphase: example\n```\n\nThen implement the change.",
            [],
            null);

        var parsed = CanonicalCommentMarkdown.Parse(CanonicalCommentMarkdown.Render(content));

        Assert.Equal(content.State, parsed.State);
        Assert.Contains("phase: example", parsed.PlanText, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePreservesOrdinaryPlanHeadings()
    {
        var content = CreateContent(
            "## Architecture\nKeep the adapter narrow.\n### Tradeoffs\nPrefer the simpler path.",
            [],
            null);

        var parsed = CanonicalCommentMarkdown.Parse(CanonicalCommentMarkdown.Render(content));

        Assert.Equal(content.PlanText, parsed.PlanText);
    }

    private static CanonicalCommentContent CreateContent(string planText, IReadOnlyList<string> decisions, string? implementationResult)
    {
        var state = new WorkflowState(
            WorkflowId.New(), WorkflowPhase.Planned, WorkflowOperationalState.Waiting, WaitingReason.PlanApproval,
            1, null, "omp-session", "agent/issue-1", "main", "abc123", DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var document = CanonicalStateSerializer.ToDocument(state, null);
        return new CanonicalCommentContent(planText, decisions, implementationResult, document);
    }
}
