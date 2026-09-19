using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IssueAgent.Domain;

namespace IssueAgent.Workflow;

/// <summary>
/// [INFERENCE] The structured JSON payload OMP returns on <c>OmpCompletedEvent</c> for a planning
/// run. No published schema for the real pinned OMP binary was available; this is this project's
/// own documented contract, matching the plan content the specification requires (§16): summary,
/// proposed changes, sequence, key decisions and rationale, alternatives, testing strategy, risks,
/// open questions. Adjust this type first if the real OMP planning result differs in shape.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanningResult(
    [property: JsonRequired, JsonPropertyName("planText")] string PlanText,
    [property: JsonRequired, JsonPropertyName("decisions")] IReadOnlyList<string> DecisionsAndRationale,
    [property: JsonPropertyName("suggestedSlug")] string? SuggestedSlug = null)
{
    public static PlanningResult Parse(string resultJson)
    {
        try
        {
            return JsonSerializer.Deserialize(resultJson, PlanningJsonContext.Default.PlanningResult)
                ?? throw new WorkflowContractException("OMP planning result JSON deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new WorkflowContractException($"OMP planning result JSON was malformed: {ex.Message}", ex);
        }
    }
}

[JsonSerializable(typeof(PlanningResult))]
internal sealed partial class PlanningJsonContext : JsonSerializerContext;

public sealed class WorkflowContractException : Exception
{
    public WorkflowContractException(string message) : base(message)
    {
    }

    public WorkflowContractException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Builds the planning prompt text from an <see cref="AgentContext"/> (specification §14,
/// §16). Selects only the portions relevant to planning; OMP's repository-native <c>AGENTS.md</c>
/// and configured supplemental instructions are honored by OMP itself once it is working in the
/// checked-out worktree, not injected here.</summary>
public static class PlanningPromptBuilder
{
    public static string BuildInitialPlanPrompt(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var builder = new StringBuilder();
        builder.AppendLine("You are planning the implementation for the following issue. Produce a thorough plan.");
        builder.AppendLine();
        AppendIssue(builder, context.PrimaryIssue, "Primary issue");
        AppendRelatedIssues(builder, context.RelatedIssues);
        builder.AppendLine("Do not modify repository files while planning.");
        AppendOutputContract(builder);
        return builder.ToString();
    }

    public static string BuildReplanPrompt(AgentContext context, IReadOnlyList<HumanComment> feedbackSinceLastPlan)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(feedbackSinceLastPlan);

        var builder = new StringBuilder();
        builder.AppendLine("The human has requested changes to the existing plan. Revise it, preserving decisions that remain valid and updating those that do not.");
        builder.AppendLine();

        if (context.CurrentPlan is { } currentPlan)
        {
            builder.AppendLine("## Current plan (revision " + currentPlan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            builder.AppendLine(currentPlan.Text);
            builder.AppendLine();
        }

        builder.AppendLine("## Human feedback since the last plan revision");
        foreach (var comment in feedbackSinceLastPlan)
        {
            builder.Append("- ").Append(comment.Author).Append(": ").AppendLine(comment.Body);
        }

        builder.AppendLine();
        AppendIssue(builder, context.PrimaryIssue, "Primary issue");
        AppendRelatedIssues(builder, context.RelatedIssues);
        builder.AppendLine("Do not modify repository files while planning.");
        AppendOutputContract(builder);
        return builder.ToString();
    }

    private static void AppendOutputContract(StringBuilder builder)
    {
        builder.AppendLine("Return only one JSON object: no Markdown fence, prose, or text before or after it.");
        builder.AppendLine("""Its exact result schema is {"planText":"string","decisions":["string"],"suggestedSlug":"string or null (optional)"}; include planText and decisions, use no other properties.""");
    }

    private static void AppendIssue(StringBuilder builder, IssueContext issue, string heading)
    {
        builder.Append("## ").Append(heading).Append(": ").AppendLine(issue.Title);
        builder.AppendLine(issue.Description);
        if (issue.Labels.Count > 0)
        {
            builder.Append("Labels: ").AppendLine(string.Join(", ", issue.Labels));
        }

        if (issue.HumanComments.Count > 0)
        {
            builder.AppendLine("Discussion:");
            foreach (var comment in issue.HumanComments)
            {
                builder.Append("- ").Append(comment.Author).Append(" (").Append(comment.CreatedAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture)).Append("): ").AppendLine(comment.Body);
            }
        }

        if (issue.Attachments.Count > 0)
        {
            builder.AppendLine("Attachments:");
            foreach (var attachment in issue.Attachments)
            {
                if (attachment.IsOmitted)
                {
                    builder.Append("- OMITTED ").Append(attachment.SafeFileName).Append(": ").AppendLine(attachment.OmissionReason);
                }
                else
                {
                    builder.Append("- ").Append(attachment.SafeFileName).Append(" (").Append(attachment.LocalPath).AppendLine(")");
                }
            }
        }

        builder.AppendLine();
    }

    private static void AppendRelatedIssues(StringBuilder builder, IReadOnlyList<RelatedIssueContext> relatedIssues)
    {
        foreach (var related in relatedIssues)
        {
            AppendIssue(builder, related.Issue, $"Related issue ({related.Relationship}, read-only context)");
        }
    }
}
