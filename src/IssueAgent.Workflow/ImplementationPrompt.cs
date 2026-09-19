using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IssueAgent.Domain;

namespace IssueAgent.Workflow;

/// <summary>
/// [INFERENCE] The structured JSON payload OMP returns on <c>OmpCompletedEvent</c> for an
/// implementation, revision, or conflict-resolution run. No published schema for the real pinned
/// OMP binary was available; this is this project's own documented contract, matching the
/// implementation decision/rationale contract the specification requires (§20): summary, key
/// changes, key decisions and rationale, tests/checks run, known failures, deviations from the
/// approved plan, risks. <see cref="IsMaterialDeviation"/> signals the workflow must pause for
/// human interaction rather than silently proceeding (§20 "material architectural/scope deviation").
/// </summary>
public sealed record ImplementationResult(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("keyChanges")] IReadOnlyList<string> KeyChanges,
    [property: JsonPropertyName("decisions")] IReadOnlyList<string> DecisionsAndRationale,
    [property: JsonPropertyName("checksRun")] IReadOnlyList<string> ChecksRun,
    [property: JsonPropertyName("knownFailures")] IReadOnlyList<string> KnownFailures,
    [property: JsonPropertyName("deviations")] IReadOnlyList<string> Deviations,
    [property: JsonPropertyName("risks")] IReadOnlyList<string> Risks,
    [property: JsonPropertyName("isMaterialDeviation")] bool IsMaterialDeviation = false,
    [property: JsonPropertyName("materialDeviationExplanation")] string? MaterialDeviationExplanation = null)
{
    public static ImplementationResult Parse(string resultJson)
    {
        try
        {
            return JsonSerializer.Deserialize(resultJson, ImplementationJsonContext.Default.ImplementationResult)
                ?? throw new WorkflowContractException("OMP implementation result JSON deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new WorkflowContractException($"OMP implementation result JSON was malformed: {ex.Message}", ex);
        }
    }

    public string RenderMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Summary.TrimEnd());

        AppendBulletSection(builder, "Key changes", KeyChanges);
        AppendBulletSection(builder, "Key decisions and rationale", DecisionsAndRationale);
        AppendBulletSection(builder, "Checks run", ChecksRun);
        AppendBulletSection(builder, "Known/pre-existing failures", KnownFailures);
        AppendBulletSection(builder, "Deviations from the approved plan", Deviations);
        AppendBulletSection(builder, "Risks and remaining considerations", Risks);

        return builder.ToString().TrimEnd();
    }

    private static void AppendBulletSection(StringBuilder builder, string heading, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.Append("**").Append(heading).AppendLine(":**");
        foreach (var item in items)
        {
            builder.Append("- ").AppendLine(item);
        }
    }
}

[JsonSerializable(typeof(ImplementationResult))]
internal sealed partial class ImplementationJsonContext : JsonSerializerContext;

/// <summary>Builds implementation, revision, and conflict-resolution prompts (specification §14, §20-21).</summary>
public static class ImplementationPromptBuilder
{
    public static string BuildImplementationPrompt(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var builder = new StringBuilder();
        builder.AppendLine("The plan below has been approved. Implement it: make the changes, run tests/checks, and commit.");
        builder.AppendLine("Do not silently make a material architectural or scope deviation from the plan; if one is discovered, report it instead of proceeding.");
        builder.AppendLine();

        if (context.CurrentPlan is { } plan)
        {
            builder.AppendLine("## Approved plan (revision " + plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            builder.AppendLine(plan.Text);
            builder.AppendLine();
        }

        AppendIssueSummary(builder, context.PrimaryIssue);
        return builder.ToString();
    }

    public static string BuildCorrectivePrompt() =>
        "The intended changes were not fully committed. Commit the remaining changes now, or explain why nothing further should be committed.";

    public static string BuildContinuationPrompt() =>
        "Continue the approved implementation after the human acknowledged the reported deviation. Inspect the current worktree, implement the accepted direction, run tests/checks, and commit. Report the resulting implementation summary and any remaining material deviation.";

    public static string BuildConflictResolutionPrompt() =>
        "The target branch advanced and conflicts were merged into your branch. Resolve the conflicts, ensure the code is correct, and commit the resolution.";

    public static string BuildRevisionPrompt(AgentContext context, IReadOnlyList<HumanComment> reviewFeedback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reviewFeedback);

        var builder = new StringBuilder();
        builder.AppendLine("The human has reviewed the pull/merge request and left the feedback below. Revise the implementation accordingly, run tests/checks, and commit.");
        builder.AppendLine();
        builder.AppendLine("## Review feedback since the previous revision");
        foreach (var comment in reviewFeedback)
        {
            builder.Append("- ").Append(comment.Author).Append(": ").AppendLine(comment.Body);
        }

        builder.AppendLine();
        AppendIssueSummary(builder, context.PrimaryIssue);
        return builder.ToString();
    }

    private static void AppendIssueSummary(StringBuilder builder, IssueContext issue)
    {
        builder.Append("## Primary issue: ").AppendLine(issue.Title);
        builder.AppendLine(issue.Description);
    }
}
