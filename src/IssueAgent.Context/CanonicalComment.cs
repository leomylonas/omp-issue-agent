using System.Text;
using System.Text.RegularExpressions;
using IssueAgent.Providers;

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
    public const string PlanStartMarker = "<!-- issue-agent:plan:start -->";
    public const string PlanEndMarker = "<!-- issue-agent:plan:end -->";
    public const string DecisionsStartMarker = "<!-- issue-agent:decisions:start -->";
    public const string DecisionsEndMarker = "<!-- issue-agent:decisions:end -->";
    public const string ResultStartMarker = "<!-- issue-agent:result:start -->";
    public const string ResultEndMarker = "<!-- issue-agent:result:end -->";

    public static string Render(CanonicalCommentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();
        builder.AppendLine(PlanStartMarker);
        builder.AppendLine("## Implementation plan");
        builder.AppendLine(content.PlanText.TrimEnd());
        builder.AppendLine(PlanEndMarker);

        if (content.DecisionsAndRationale.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(DecisionsStartMarker);
            builder.AppendLine("### Key decisions and rationale");
            foreach (var decision in content.DecisionsAndRationale)
            {
                if (string.IsNullOrWhiteSpace(decision))
                {
                    continue;
                }

                var lines = decision.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                builder.Append("- ").AppendLine(lines[0]);
                for (var i = 1; i < lines.Length; i++)
                {
                    builder.Append("  ").AppendLine(lines[i]);
                }
            }
            builder.AppendLine(DecisionsEndMarker);
        }

        if (content.ImplementationResult is { Length: > 0 } implementationResult)
        {
            builder.AppendLine();
            builder.AppendLine(ResultStartMarker);
            builder.AppendLine("## Implementation result");
            builder.AppendLine(implementationResult.TrimEnd());
            builder.AppendLine(ResultEndMarker);
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

    /// <summary>True when <paramref name="body"/> contains the unique hidden locator marker on
    /// its own line. The marker is intentionally recognized even when a corruption warning or
    /// other appended operator text follows it, so an escalated canonical comment remains
    /// discoverable on the next poll.</summary>
    public static bool IsCanonicalComment(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Any(line => string.Equals(line.Trim(), StateLocatorMarker, StringComparison.Ordinal));
    }

    /// <summary>Returns whether a comment is canonical workflow state from the provider identity
    /// trusted to publish it. A human copying the locator marker remains ordinary OMP-visible input.</summary>
    public static bool IsAuthoritativeCanonicalComment(ProviderComment comment, string authoritativeAuthor)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativeAuthor);
        return IsCanonicalComment(comment.Body) &&
            string.Equals(comment.AuthorLogin, authoritativeAuthor, StringComparison.OrdinalIgnoreCase);
    }

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

        var stateMatches = StateFencePattern().Matches(body);
        if (stateMatches.Count != 1)
        {
            throw new CanonicalCommentCorruptException(
                stateMatches.Count == 0
                    ? "Canonical comment is missing its generated fenced YAML state block."
                    : "Canonical comment contains duplicate generated fenced YAML state blocks.");
        }
        var stateMatch = stateMatches[0];

        CanonicalStateDocument state;
        try
        {
            state = CanonicalStateSerializer.Deserialize(stateMatch.Groups["yaml"].Value);
        }
        catch (CanonicalStateException ex)
        {
            throw new CanonicalCommentCorruptException($"Canonical comment state YAML is invalid: {ex.Message}", ex);
        }

        var planMatches = PlanSectionPattern().Matches(body);
        if (planMatches.Count != 1)
        {
            throw new CanonicalCommentCorruptException(
                planMatches.Count == 0
                    ? "Canonical comment is missing its generated implementation plan section."
                    : "Canonical comment contains duplicate generated implementation plan sections.");
        }
        var planMatch = planMatches[0];
        var planText = planMatch.Groups["plan"].Value.TrimEnd();

        var decisionsMatches = DecisionsSectionPattern().Matches(body);
        if (decisionsMatches.Count > 1)
        {
            throw new CanonicalCommentCorruptException("Canonical comment contains duplicate generated decisions sections.");
        }
        var decisions = new List<string>();
        if (decisionsMatches.Count == 1)
        {
            foreach (Match bullet in BulletLinePattern().Matches(decisionsMatches[0].Groups["decisions"].Value))
            {
                decisions.Add(JoinContinuationLines(bullet.Groups["text"].Value));
            }
        }

        var implementationMatches = ImplementationResultSectionPattern().Matches(body);
        if (implementationMatches.Count > 1)
        {
            throw new CanonicalCommentCorruptException("Canonical comment contains duplicate generated implementation-result sections.");
        }
        string? implementationResult = implementationMatches.Count == 1
            ? implementationMatches[0].Groups["result"].Value.Trim()
            : null;

        return new CanonicalCommentContent(planText, decisions, implementationResult, state);

    }

    [GeneratedRegex(@"<details>\s*<summary>Agent state</summary>.*?```yaml\r?\n(?<yaml>.*?)```\s*</details>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StateFencePattern();

    [GeneratedRegex(@"<!-- issue-agent:plan:start -->\r?\n## Implementation plan\r?\n(?<plan>.*?)\r?\n<!-- issue-agent:plan:end -->", RegexOptions.Singleline)]
    private static partial Regex PlanSectionPattern();

    [GeneratedRegex(@"<!-- issue-agent:decisions:start -->\r?\n### Key decisions and rationale\r?\n(?<decisions>.*?)\r?\n<!-- issue-agent:decisions:end -->", RegexOptions.Singleline)]
    private static partial Regex DecisionsSectionPattern();

    [GeneratedRegex(@"<!-- issue-agent:result:start -->\r?\n## Implementation result\r?\n(?<result>.*?)\r?\n<!-- issue-agent:result:end -->", RegexOptions.Singleline)]
    private static partial Regex ImplementationResultSectionPattern();

    [GeneratedRegex(@"^-\s+(?<text>.+?)(?=\r?\n-\s|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex BulletLinePattern();

    private static string JoinContinuationLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            lines[i] = lines[i].StartsWith("  ", StringComparison.Ordinal) ? lines[i][2..] : lines[i];
        }

        return string.Join('\n', lines).Trim();
    }
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
