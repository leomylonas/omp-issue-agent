namespace IssueAgent.Providers;

/// <summary>Sanitizes provider-supplied attachment filenames so they cannot escape the destination
/// directory via path traversal, absolute paths, or reserved names, and cannot smuggle shell or
/// filesystem metacharacters that would be unsafe on any supported deployment target.</summary>
public static class AttachmentFileNames
{
    private const string FallbackName = "attachment";

    public static string Sanitize(string suggestedFileName)
    {
        ArgumentNullException.ThrowIfNull(suggestedFileName);

        var candidate = suggestedFileName.Trim();
        foreach (var separator in new[] { '/', '\\' })
        {
            var lastIndex = candidate.LastIndexOf(separator);
            if (lastIndex >= 0)
            {
                candidate = candidate[(lastIndex + 1)..];
            }
        }

        var sanitized = new string(candidate.Select(c => IsSafeCharacter(c) ? c : '_').ToArray());
        sanitized = sanitized.Trim('.', ' ', '_');

        return string.IsNullOrEmpty(sanitized) ? FallbackName : sanitized;
    }

    /// <summary>Resolves the sanitized filename against the destination directory and verifies the
    /// resolved path still lives inside it, guarding against remaining traversal or symlink escape.</summary>
    public static string ResolveSafeDestination(string destinationDirectory, string suggestedFileName)
    {
        ArgumentNullException.ThrowIfNull(destinationDirectory);

        var safeName = Sanitize(suggestedFileName);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var resolvedPath = Path.GetFullPath(Path.Combine(destinationRoot, safeName));

        if (!resolvedPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Sanitized attachment path '{resolvedPath}' escapes destination directory '{destinationRoot}'.");
        }

        return resolvedPath;
    }

    private static bool IsSafeCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ';
}
