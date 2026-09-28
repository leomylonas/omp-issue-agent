using System.Text.RegularExpressions;

namespace IssueAgent.Providers;

/// <summary>
/// Extracts candidate attachment links from human-authored Markdown bodies. IssueAgent never
/// crawls ordinary webpages; only Markdown-embedded image/link syntax pointing at provider-owned
/// attachment endpoints or obvious direct-file HTTP(S) URLs is considered.
/// </summary>
public static partial class MarkdownAttachmentScanner
{
    // A filename extension is the direct-file signal, regardless of the particular file format.
    // Attachment handling is deliberately content-agnostic (§15): archives and unknown types are
    // retained as files, never extracted or interpreted.

    public static IEnumerable<Uri> ScanLinks(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        foreach (Match match in MarkdownLinkPattern().Matches(body))
        {
            if (TryCreateAttachmentUri(match.Groups["url"].Value, out var uri))
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
        var fileName = Path.GetFileName(uri.AbsolutePath);
        return !string.IsNullOrWhiteSpace(fileName) &&
               !fileName.EndsWith('.') &&
               Path.GetExtension(fileName).Length > 1;
    }

    private static bool TryCreateAttachmentUri(string candidate, out Uri uri)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https")
        {
            uri = absolute;
            return true;
        }

        if (Uri.TryCreate(candidate, UriKind.Relative, out var relative))
        {
            uri = relative;
            return true;
        }

        uri = null!;
        return false;
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

    [GeneratedRegex(@"!?\[[^\]]*\]\((?<url>[^\s)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"(?<!\()https?://[^\s)>\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex BareUrlPattern();
}
