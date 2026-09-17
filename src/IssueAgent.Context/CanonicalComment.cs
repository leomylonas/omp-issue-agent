using System.Text;
using System.Text.RegularExpressions;

namespace IssueAgent.Context;

/// <summary>The recognizable header, plan/decisions/implementation-result sections, and machine
/// state that make up the one canonical IssueAgent comment (specification §17). Replanning edits
/// this same content in place; it is never recreated.</summary>
public sealed record CanonicalCommentContent(
    string PlanText,
    IReadOnlyList<string> DecisionsAndRationale,
    string? ImplementationResult,
    CanonicalStateDocument State);

/// <summary>Renders and parses the canonical IssueAgent comment. Human edits to this comment are
/// not authoritative control input; a comment that cannot be parsed is treated as corrupted rather
/// than guessed at.</summary>
public static partial class CanonicalCommentMarkdown
{
    public const string Header = "**IssueAgent — managed automatically**";
    public const string StateLocatorMarker = "<!-- issue-agent:state -->";

    public static string Render(CanonicalCommentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();
        builder.AppendLine("## Implementation plan");
        builder.AppendLine(content.PlanText.TrimEnd());

        if (content.DecisionsAndRationale.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Key decisions and rationale");
            foreach (var decision in content.DecisionsAndRationale)
            {
                builder.Append("- ").AppendLine(decision);
            }
        }

        if (content.ImplementationResult is { Length: > 0 } implementationResult)
        {
            builder.AppendLine();
            builder.AppendLine("## Implementation result");
            builder.AppendLine(implementationResult.TrimEnd());
        }

        builder.AppendLine();
        builder.AppendLine("<details>");
        builder.AppendLine("<summary>Agent state</summary>");
        builder.AppendLine();
        builder.AppendLine("```yaml");
        builder.AppendLine(CanonicalStateSerializer.Serialize(content.State));
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("</details>");
        builder.AppendLine(StateLocatorMarker);

        return builder.ToString();
    }

    /// <summary>True when <paramref name="body"/> is a managed IssueAgent comment, identified by its
    /// hidden locator marker. Callers use this to distinguish the canonical comment from ordinary
    /// human comments when scanning an issue's comment list.</summary>
    public static bool IsCanonicalComment(string body) => body.Contains(StateLocatorMarker, StringComparison.Ordinal);

    /// <summary>Parses a canonical comment body. Throws <see cref="CanonicalCommentCorruptException"/>
    /// when the locator marker, state fence, or YAML content cannot be recovered; callers must treat
    /// that as corrupted state requiring conservative reconciliation or human intervention, never a
    /// guess.</summary>
    public static CanonicalCommentContent Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!IsCanonicalComment(body))
        {
            throw new CanonicalCommentCorruptException("Comment body does not contain the canonical state locator marker.");
        }

        var stateMatch = StateFencePattern().Match(body);
        if (!stateMatch.Success)
        {
            throw new CanonicalCommentCorruptException("Canonical comment is missing its fenced YAML state block.");
        }

        CanonicalStateDocument state;
        try
        {
            state = CanonicalStateSerializer.Deserialize(stateMatch.Groups["yaml"].Value);
        }
        catch (CanonicalStateException ex)
        {
            throw new CanonicalCommentCorruptException($"Canonical comment state YAML is invalid: {ex.Message}", ex);
        }

        var planMatch = PlanSectionPattern().Match(body);
        if (!planMatch.Success)
        {
            throw new CanonicalCommentCorruptException("Canonical comment is missing its '## Implementation plan' section.");
        }

        var planText = planMatch.Groups["plan"].Value.TrimEnd();

        var decisions = new List<string>();
        var decisionsMatch = DecisionsSectionPattern().Match(body);
        if (decisionsMatch.Success)
        {
            foreach (Match bullet in BulletLinePattern().Matches(decisionsMatch.Groups["decisions"].Value))
            {
                decisions.Add(bullet.Groups["text"].Value.Trim());
            }
        }

        string? implementationResult = null;
        var implementationMatch = ImplementationResultSectionPattern().Match(body);
        if (implementationMatch.Success)
        {
            implementationResult = implementationMatch.Groups["result"].Value.Trim();
        }

        return new CanonicalCommentContent(planText, decisions, implementationResult, state);
    }

    [GeneratedRegex(@"```yaml\r?\n(?<yaml>.*?)```", RegexOptions.Singleline)]
    private static partial Regex StateFencePattern();

    [GeneratedRegex(@"^## Implementation plan\r?\n(?<plan>.*?)(?=\r?\n##[ #]|\r?\n<details>|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex PlanSectionPattern();

    [GeneratedRegex(@"^### Key decisions and rationale\r?\n(?<decisions>.*?)(?=\r?\n##|\r?\n<details>|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex DecisionsSectionPattern();

    [GeneratedRegex(@"^## Implementation result\r?\n(?<result>.*?)(?=\r?\n<details>|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex ImplementationResultSectionPattern();

    [GeneratedRegex(@"^-\s+(?<text>.+)$", RegexOptions.Multiline)]
    private static partial Regex BulletLinePattern();
}

public sealed class CanonicalCommentCorruptException : Exception
{
    public CanonicalCommentCorruptException(string message) : base(message)
    {
    }

    public CanonicalCommentCorruptException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
