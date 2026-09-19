namespace IssueAgent.Observability;

/// <summary>Redacts outbound HTTP request URIs before they are attached to trace spans
/// (specification §29: never log or export authenticated URLs, tokens, or query-string secrets).
/// Only scheme and host:port survive; path, query string, and any embedded user-info credentials
/// are discarded.</summary>
public static class HttpSpanRedactor
{
    public static string RedactUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return $"{uri.Scheme}://{uri.Authority}/";
    }
}
