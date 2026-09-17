using System.Text;
using System.Text.RegularExpressions;

namespace IssueAgent.Workflow;

/// <summary>Derives the default agent branch name <c>agent/issue-{number}-{slug}</c> (specification
/// §10). Titles are slugified and truncated deterministically; OMP is not asked for a slug in this
/// implementation, which the specification allows as one option for long/unsuitable titles.</summary>
public static partial class BranchNaming
{
    private const int MaxSlugLength = 48;

    public static string DeriveBranchName(long issueNumber, string issueTitle)
    {
        var slug = Slugify(issueTitle);
        return $"agent/issue-{issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{slug}";
    }

    public static string Slugify(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var lowered = title.ToLowerInvariant();
        var withDashes = NonAlphanumericPattern().Replace(lowered, "-");
        var collapsed = CollapseDashesPattern().Replace(withDashes, "-").Trim('-');

        if (collapsed.Length == 0)
        {
            return "issue";
        }

        if (collapsed.Length <= MaxSlugLength)
        {
            return collapsed;
        }

        var truncated = collapsed[..MaxSlugLength].Trim('-');
        return truncated.Length == 0 ? "issue" : truncated;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericPattern();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex CollapseDashesPattern();
}
