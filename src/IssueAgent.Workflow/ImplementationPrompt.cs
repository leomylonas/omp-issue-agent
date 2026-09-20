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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImplementationResult(
    [property: JsonRequired, JsonPropertyName("summary")] string Summary,
    [property: JsonRequired, JsonPropertyName("keyChanges")] IReadOnlyList<string> KeyChanges,
    [property: JsonRequired, JsonPropertyName("decisions")] IReadOnlyList<string> DecisionsAndRationale,
    [property: JsonRequired, JsonPropertyName("checksRun")] IReadOnlyList<string> ChecksRun,
    [property: JsonRequired, JsonPropertyName("knownFailures")] IReadOnlyList<string> KnownFailures,
    [property: JsonRequired, JsonPropertyName("deviations")] IReadOnlyList<string> Deviations,
    [property: JsonRequired, JsonPropertyName("risks")] IReadOnlyList<string> Risks,
    [property: JsonPropertyName("isMaterialDeviation")] bool IsMaterialDeviation = false,
    [property: JsonPropertyName("materialDeviationExplanation")] string? MaterialDeviationExplanation = null)
{
    public static ImplementationResult Parse(string resultJson)
    {
        try
        {
            var result = JsonSerializer.Deserialize(resultJson, ImplementationJsonContext.Default.ImplementationResult)
                ?? throw new WorkflowContractException("OMP implementation result JSON deserialized to null.");
            if (string.IsNullOrWhiteSpace(result.Summary) ||
                result.KeyChanges is null ||
                result.DecisionsAndRationale is null ||
                result.ChecksRun is null ||
                result.KnownFailures is null ||
                result.Deviations is null ||
                result.Risks is null ||
                (result.IsMaterialDeviation && string.IsNullOrWhiteSpace(result.MaterialDeviationExplanation)) ||
                (!result.IsMaterialDeviation && result.MaterialDeviationExplanation is not null) ||
                ContainsBlankItem(result.KeyChanges) ||
                ContainsBlankItem(result.DecisionsAndRationale) ||
                ContainsBlankItem(result.ChecksRun) ||
                ContainsBlankItem(result.KnownFailures) ||
                ContainsBlankItem(result.Deviations) ||
                ContainsBlankItem(result.Risks))
            {
                throw new WorkflowContractException("OMP implementation result JSON contains null, blank, or inconsistent required content.");
            }

            return result;
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

    private static bool ContainsBlankItem(IReadOnlyList<string> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                return true;
            }
        }

        return false;
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
        AppendOutputContract(builder);
        return builder.ToString();
    }

    public static string BuildCorrectivePrompt() =>
        "The intended changes were not fully committed. Commit the remaining changes now, or explain why nothing further should be committed.\n\n" +
        OutputContract;

    public static string BuildContinuationPrompt() =>
        "Continue the approved implementation after the human acknowledged the reported deviation. Inspect the current worktree, implement the accepted direction, run tests/checks, and commit. Report the resulting implementation summary and any remaining material deviation.\n\n" +
        OutputContract;

    public static string BuildConflictResolutionPrompt() =>
        "The target branch advanced and conflicts were merged into your branch. Resolve the conflicts, ensure the code is correct, and commit the resolution.\n\n" +
        OutputContract;

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
            builder.Append("- ").Append(comment.Author)
                .Append(" (resolved: ").Append(comment.IsResolved ? "true" : "false")
                .Append("): ").AppendLine(comment.Body);
        }

        builder.AppendLine();
        AppendReviewAttachments(builder, context.PullOrMergeRequest?.Attachments);
        builder.AppendLine();
        AppendIssueSummary(builder, context.PrimaryIssue);
        AppendOutputContract(builder);
        return builder.ToString();
    }

    private static void AppendReviewAttachments(StringBuilder builder, IReadOnlyList<AttachmentReference>? attachments)
    {
        if (attachments is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("## Review attachments");
        foreach (var attachment in attachments)
        {
            if (attachment.IsOmitted)
            {
                builder.Append("- OMITTED ").Append(attachment.SafeFileName)
                    .Append(" (").Append(attachment.Provenance).Append("): ")
                    .AppendLine(attachment.OmissionReason);
            }
            else
            {
                builder.Append("- AVAILABLE ").Append(attachment.SafeFileName)
                    .Append(" (").Append(attachment.Provenance).Append("): ")
                    .AppendLine(attachment.LocalPath);
            }
        }
    }

    private const string OutputContract =
        """Return only one JSON object: no Markdown fence, prose, or text before or after it. Its exact result schema is {"summary":"string","keyChanges":["string"],"decisions":["string"],"checksRun":["string"],"knownFailures":["string"],"deviations":["string"],"risks":["string"],"isMaterialDeviation":"boolean (optional)","materialDeviationExplanation":"string or null (optional)"}; include every non-optional property and use no others.""";

    private static void AppendOutputContract(StringBuilder builder) => builder.AppendLine(OutputContract);

    private static void AppendIssueSummary(StringBuilder builder, IssueContext issue)
    {
        builder.Append("## Primary issue: ").AppendLine(issue.Title);
        builder.AppendLine(issue.Description);
    }
}
