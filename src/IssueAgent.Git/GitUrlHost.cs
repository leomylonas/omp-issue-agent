namespace IssueAgent.Git;

/// <summary>Extracts remote transport authorities from HTTPS, SSH, or SCP-like
/// (<c>user@host:path</c>) Git URLs. Credential forwarding is scoped to the transport scheme,
/// host, and port rather than merely a hostname.</summary>
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

    /// <summary>Returns the scheme, host, and effective port that identify a Git transport
    /// endpoint. SCP-like URLs are SSH endpoints. User names are intentionally excluded because
    /// Git authentication supplies them independently.</summary>
    public static string? TryGetTransportAuthority(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var port = uri.IsDefaultPort
                ? uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase) ? 22 : uri.Port
                : uri.Port;
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{port}";
        }

        var at = url.IndexOf('@');
        var colon = url.IndexOf(':', at + 1);
        if (at > 0 && colon > at + 1)
        {
            var host = url[(at + 1)..colon];
            return string.IsNullOrWhiteSpace(host) ? null : $"ssh://{host.ToLowerInvariant()}:22";
        }

        return null;
    }
}
