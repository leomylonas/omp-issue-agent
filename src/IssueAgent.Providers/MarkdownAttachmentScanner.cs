using System.Text.RegularExpressions;

namespace IssueAgent.Providers;

/// <summary>
/// Extracts candidate attachment links from human-authored Markdown bodies. IssueAgent never
/// crawls ordinary webpages; only Markdown-embedded image/link syntax pointing at provider-owned
/// attachment endpoints or obvious direct-file HTTP(S) URLs is considered.
/// </summary>
public static partial class MarkdownAttachmentScanner
{
    private static readonly string[] DirectFileExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg",
        ".pdf", ".zip", ".tar", ".gz", ".log", ".txt", ".csv",
        ".json", ".yaml", ".yml", ".patch", ".diff",
    ];

    public static IEnumerable<Uri> ScanLinks(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        foreach (Match match in MarkdownLinkPattern().Matches(body))
        {
            if (TryCreateHttpUri(match.Groups["url"].Value, out var uri))
            {
                yield return uri;
            }
        }

        foreach (Match match in BareUrlPattern().Matches(body))
        {
            if (TryCreateHttpUri(match.Value, out var uri))
            {
                yield return uri;
            }
        }
    }

    public static bool IsDirectFileLink(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var path = uri.AbsolutePath;
        return DirectFileExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryCreateHttpUri(string candidate, out Uri uri)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    [GeneratedRegex(@"!?\[[^\]]*\]\((?<url>https?://[^\s)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"(?<!\()https?://[^\s)>\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex BareUrlPattern();
}
