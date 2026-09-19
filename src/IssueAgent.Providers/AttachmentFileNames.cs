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
        Directory.CreateDirectory(destinationRoot);
        EnsureNoSymbolicLink(destinationRoot);
        var resolvedPath = Path.GetFullPath(Path.Combine(destinationRoot, safeName));
        if (!resolvedPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Sanitized attachment path '{resolvedPath}' escapes destination directory '{destinationRoot}'.");
        }
        if ((File.Exists(resolvedPath) || Directory.Exists(resolvedPath)) &&
            File.GetAttributes(resolvedPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"Attachment destination '{resolvedPath}' is a symbolic link.");
        }

        var extension = Path.GetExtension(safeName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        for (var suffix = 1; File.Exists(resolvedPath); suffix++)
        {
            resolvedPath = Path.Combine(destinationRoot, $"{stem}-{suffix}{extension}");
        }
        return resolvedPath;
    }

    private static void EnsureNoSymbolicLink(string path)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var segment in path[Path.GetPathRoot(path)!.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Attachment destination directory '{path}' contains a symbolic link.");
            }
        }
    }

    private static bool IsSafeCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ';
}
