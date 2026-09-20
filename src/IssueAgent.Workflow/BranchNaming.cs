using System.Text;
using System.Text.RegularExpressions;

namespace IssueAgent.Workflow;

/// <summary>Derives the default agent branch name <c>agent/issue-{number}-{slug}</c> (specification
/// §10). Long and unsuitable titles retain a deterministic fallback until planning supplies a
/// concise OMP suggestion, which is sanitized before it becomes a branch name.</summary>
public static partial class BranchNaming
{
    private const int MaxSlugLength = 48;

    public static string DeriveBranchName(long issueNumber, string issueTitle)
    {
        var slug = Slugify(issueTitle);
        return $"agent/issue-{issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{slug}";
    }

    /// <summary>Returns whether the title's deterministic slug lost meaningful information and
    /// planning should prefer a valid OMP suggestion.</summary>
    public static bool RequiresSuggestedSlug(string issueTitle)
    {
        ArgumentNullException.ThrowIfNull(issueTitle);
        var collapsed = CollapseDashesPattern().Replace(NonAlphanumericPattern().Replace(issueTitle.ToLowerInvariant(), "-"), "-").Trim('-');
        return collapsed.Length == 0 || collapsed.Length > MaxSlugLength;
    }

    /// <summary>Builds a safe issue branch from an OMP suggestion when the title needs one.
    /// Empty or content-free suggestions do not select a branch, preserving the retained branch.</summary>
    public static string? TryDeriveSuggestedBranchName(long issueNumber, string issueTitle, string? suggestedSlug)
    {
        if (!RequiresSuggestedSlug(issueTitle) || string.IsNullOrWhiteSpace(suggestedSlug))
        {
            return null;
        }

        var slug = Slugify(suggestedSlug);
        return slug == "issue"
            ? null
            : $"agent/issue-{issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{slug}";
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
