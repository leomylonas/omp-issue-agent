namespace IssueAgent.Git;

/// <summary>Extracts the authority host from an HTTPS or SCP-like (<c>user@host:path</c>) Git remote
/// URL. Used to scope credentials to the host they were configured for (specification §11) rather
/// than forwarding them to whatever host a URL happens to name.</summary>
public static class GitUrlHost
{
    public static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }

        var at = url.IndexOf('@');
        var colon = url.IndexOf(':', at + 1);
        if (at > 0 && colon > at + 1)
        {
            var host = url[(at + 1)..colon];
            return string.IsNullOrWhiteSpace(host) ? null : host;
        }

        return null;
    }
}
