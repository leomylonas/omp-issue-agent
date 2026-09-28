using System.Text.RegularExpressions;

namespace IssueAgent.Providers;

/// <summary>
/// Extracts candidate attachment links from human-authored Markdown bodies. IssueAgent never
/// crawls ordinary webpages; only Markdown-embedded image/link syntax pointing at provider-owned
/// attachment endpoints or obvious direct-file HTTP(S) URLs is considered.
/// </summary>
public static partial class MarkdownAttachmentScanner
{
    // Extensions are an unambiguous direct-file signal. Extensionless links must use a bounded
    // download-shaped path and later prove an attachment Content-Disposition; this admits unknown
    // file types without treating arbitrary webpages as files.

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

    /// <summary>Returns whether an extensionless URL has a deliberately narrow download route
    /// shape and therefore may be fetched only if the response says it is an attachment.</summary>
    public static bool IsContentDispositionAttachmentCandidate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("http" or "https") || IsDirectFileLink(uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length is > 0 and <= 8 &&
            segments.Any(segment => segment.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals("downloads", StringComparison.OrdinalIgnoreCase));
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
