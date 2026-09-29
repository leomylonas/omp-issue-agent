using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Context;

public sealed record AttachmentLimits
{
    public long MaxAttachmentSizeBytes { get; init; } = 25 * 1024 * 1024;

    public long MaxTotalSizeBytes { get; init; } = 100 * 1024 * 1024;

}

/// <summary>
/// Discovers and downloads attachments referenced from human-authored Markdown bodies
/// (specification §15). Discovery is a pure scan of already-fetched text; it never crawls ordinary
/// webpages. Downloads stop once <see cref="AttachmentLimits.MaxTotalSizeBytes"/> is reached for the
/// primary workflow context; oversized or budget-exceeding attachments are omitted with a reason
/// rather than failing the workflow.
/// </summary>
public sealed class AttachmentPipeline(
    IGitProvider provider,
    AttachmentLimits limits,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolveHostAddressesAsync = null)
{
    private const string ManifestFileName = ".issue-agent-attachment-manifest.json";
    private readonly Dictionary<string, DownloadedAttachment> downloadedAttachments = new(StringComparer.Ordinal);
    private readonly HashSet<string> loadedManifestDirectories = new(StringComparer.Ordinal);
    private readonly Lock downloadedAttachmentsLock = new();
    /// <summary>Scans <paramref name="body"/> for candidate attachment links and downloads each into
    /// <paramref name="destinationDirectory"/>, tracking cumulative usage against
    /// <paramref name="remainingBudget"/>. Returns one <see cref="AttachmentReference"/> per
    /// candidate link, including omitted ones, and decrements <paramref name="remainingBudget"/> by
    /// the bytes actually downloaded.</summary>
    public async Task<IReadOnlyList<AttachmentReference>> ProcessAsync(
        string body,
        AttachmentSource source,
        string destinationDirectory,
        RemainingBudget remainingBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(remainingBudget);

        EnsureManifestLoaded(destinationDirectory);

        var results = new List<AttachmentReference>();

        string CacheKey(Uri url) => BuildCacheKey(destinationDirectory, url);
        foreach (var candidateUrl in MarkdownAttachmentScanner.ScanLinks(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = provider.ResolveAttachmentUrl(candidateUrl);
            if (url is null)
            {
                continue;
            }

            var isTrusted = provider.IsTrustedAttachmentHost(url);
            var isDirectFile = MarkdownAttachmentScanner.IsDirectFileLink(url);
            var requiresContentDisposition = !isTrusted && !isDirectFile;
            if (requiresContentDisposition && !MarkdownAttachmentScanner.IsContentDispositionAttachmentCandidate(url))
            {
                // Not a provider-owned attachment, an obvious direct-file link, or a narrowly
                // shaped extensionless download link: this is an ordinary webpage reference
                // IssueAgent must not crawl.
                continue;
            }

            var suggestedFileName = Path.GetFileName(url.AbsolutePath) is { Length: > 0 } name ? name : "attachment";
            var providerAttachment = new ProviderAttachment(
                url,
                suggestedFileName,
                SizeBytes: null,
                source,
                isTrusted,
                RequiresAttachmentContentDisposition: requiresContentDisposition,
                ValidateRedirectDestinationAsync: (redirectUrl, token) =>
                    ValidateAnonymousDestinationAsync(redirectUrl, resolveHostAddressesAsync, token));

            if (TryGetCachedAttachment(CacheKey(url), out var cached))
            {
                results.Add(new AttachmentReference(
                    url.ToString(),
                    cached.SafeFileName,
                    cached.LocalPath,
                    DescribeProvenance(source),
                    cached.SizeBytes));
                continue;
            }

            if (!isTrusted)
            {
                var validatedAddresses = await ValidateAnonymousDestinationAsync(url, resolveHostAddressesAsync, cancellationToken).ConfigureAwait(false);
                if (validatedAddresses is null)
                {
                    results.Add(Omitted(providerAttachment, "Attachment destination resolves to a private or otherwise unsafe network address."));
                    continue;
                }

                // Carries the exact validated address set through to the anonymous HttpClient's
                // connect callback, which pins the real connection to it — closing the DNS-
                // rebinding TOCTOU window between this check and the actual download (specification
                // §15). A round-robin/failover address still in this validated set is unaffected;
                // only a genuinely new address DNS returns later is rejected.
                providerAttachment = providerAttachment with { ValidatedAddresses = validatedAddresses };
            }

            if (remainingBudget.Remaining <= 0)
            {
                results.Add(Omitted(providerAttachment, "Total attachment budget for this workflow context was already exhausted."));
                continue;
            }

            var perAttachmentCap = Math.Min(limits.MaxAttachmentSizeBytes, remainingBudget.Remaining);
            MakeRetainedAttachmentDirectoryWritable(destinationDirectory);
            // Providers resolve the same safe, collision-free path before opening the stream. Keep
            // that path so the orchestration boundary can remove a file left by a transport timeout
            // or final streaming failure after the provider's retries are exhausted.
            var partialPath = AttachmentFileNames.ResolveSafeDestination(destinationDirectory, providerAttachment.SuggestedFileName);

            try
            {
                var downloaded = await provider
                    .DownloadAttachmentAsync(providerAttachment, destinationDirectory, perAttachmentCap, cancellationToken)
                    .ConfigureAwait(false);

                if (!HasValidDownloadedSize(downloaded, perAttachmentCap))
                {
                    DeletePartialFile(downloaded.LocalPath);
                    results.Add(Omitted(providerAttachment, $"Attachment exceeds the {perAttachmentCap}-byte limit for this download."));
                    continue;
                }

                downloaded = downloaded with { ContentDigest = ComputeContentDigest(downloaded.LocalPath) };
                remainingBudget.Consume(downloaded.SizeBytes);
                CacheDownloadedAttachment(destinationDirectory, url, downloaded);
                results.Add(new AttachmentReference(
                    url.ToString(),
                    downloaded.SafeFileName,
                    downloaded.LocalPath,
                    DescribeProvenance(source),
                    downloaded.SizeBytes));
            }
            catch (AttachmentNotClassifiedException)
            {
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, "Attachment download response was not marked as an attachment."));
            }
            catch (AttachmentTooLargeException)
            {
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, $"Attachment exceeds the {perAttachmentCap}-byte limit for this download."));
            }
            catch (AttachmentRedirectRejectedException)
            {
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, "Attachment redirect was rejected because its destination was unsafe or exceeded the redirect limit."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DeletePartialFile(partialPath);
                throw;
            }
            catch (OperationCanceledException)
            {
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, "Attachment download failed: OperationCanceledException."));
            }
            catch (IOException)
            {
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, "Attachment download failed: IOException."));
            }
            catch (HttpRequestException exception)
            {
                // The anonymous client still has auto-redirect disabled: redirects are followed only
                // by the provider after each destination passes SSRF validation and address pinning.
                // Other transport failures omit this one link without aborting the workflow context.
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, $"Attachment download failed: {exception.StatusCode?.ToString() ?? exception.GetType().Name}."));
            }
            finally
            {
                ProtectRetainedAttachmentDirectory(destinationDirectory);
            }
        }

        return results;
    }

    /// <summary>Restores the retained attachment manifest so a reconstructed context builder
    /// preserves both the already-downloaded files and their workflow-wide budget consumption.</summary>
    public RemainingBudget CreateRemainingBudget(string destinationDirectory)
    {
        EnsureManifestLoaded(destinationDirectory);
        lock (downloadedAttachmentsLock)
        {
            var prefix = $"{Path.GetFullPath(destinationDirectory)}\n";
            var downloaded = downloadedAttachments
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(entry => entry.Value)
                .ToArray();
            return new RemainingBudget(
                limits.MaxTotalSizeBytes,
                downloaded.Aggregate(0L, (total, attachment) => total > long.MaxValue - attachment.SizeBytes ? long.MaxValue : total + attachment.SizeBytes));
        }
    }

    private void EnsureManifestLoaded(string destinationDirectory)
    {
        var fullDestinationDirectory = Path.GetFullPath(destinationDirectory);
        lock (downloadedAttachmentsLock)
        {
            if (!loadedManifestDirectories.Add(fullDestinationDirectory))
            {
                return;
            }

            var manifestPath = Path.Combine(fullDestinationDirectory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return;
            }

            List<AttachmentManifestEntry>? entries;
            try
            {
                entries = System.Text.Json.JsonSerializer.Deserialize<List<AttachmentManifestEntry>>(File.ReadAllText(manifestPath));
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }

            var restoredTotalSize = 0L;
            var restoredFileNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entries ?? [])
            {
                if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var url) ||
                    !IsSafeFileName(entry.SafeFileName) ||
                    entry.SizeBytes < 0 ||
                    entry.SizeBytes > limits.MaxAttachmentSizeBytes ||
                    entry.SizeBytes > limits.MaxTotalSizeBytes - restoredTotalSize ||
                    restoredFileNames.Contains(entry.SafeFileName))
                {
                    continue;
                }

                var localPath = Path.GetFullPath(Path.Combine(fullDestinationDirectory, entry.SafeFileName));
                if (Path.GetDirectoryName(localPath) != fullDestinationDirectory)
                {
                    continue;
                }

                var file = new FileInfo(localPath);
                if (!file.Exists ||
                    file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    file.Length != entry.SizeBytes ||
                    !HasMatchingContentDigest(localPath, entry.ContentDigest))
                {
                    continue;
                }

                var cacheKey = BuildCacheKey(fullDestinationDirectory, url);
                if (downloadedAttachments.ContainsKey(cacheKey))
                {
                    continue;
                }

                restoredFileNames.Add(entry.SafeFileName);
                downloadedAttachments[cacheKey] =
                    new DownloadedAttachment(localPath, entry.SafeFileName, entry.SizeBytes, entry.ContentDigest);
                restoredTotalSize += entry.SizeBytes;
            }
            ProtectRetainedAttachmentDirectory(fullDestinationDirectory);
        }
    }

    private void CacheDownloadedAttachment(string destinationDirectory, Uri url, DownloadedAttachment downloaded)
    {
        var fullDestinationDirectory = Path.GetFullPath(destinationDirectory);
        lock (downloadedAttachmentsLock)
        {
            downloadedAttachments[BuildCacheKey(fullDestinationDirectory, url)] = downloaded;
            Directory.CreateDirectory(fullDestinationDirectory);
            var prefix = $"{fullDestinationDirectory}\n";
            var manifest = downloadedAttachments
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(entry => new AttachmentManifestEntry(
                    entry.Key[prefix.Length..],
                    entry.Value.SafeFileName,
                    entry.Value.SizeBytes,
                    entry.Value.ContentDigest))
                .ToArray();
            var manifestPath = Path.Combine(fullDestinationDirectory, ManifestFileName);
            var temporaryPath = Path.Combine(fullDestinationDirectory, $".{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, System.Text.Json.JsonSerializer.Serialize(manifest));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
    }

    private static string BuildCacheKey(string destinationDirectory, Uri url) =>
        $"{Path.GetFullPath(destinationDirectory)}\n{url.AbsoluteUri}";

    private static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal);

    private bool TryGetCachedAttachment(string key, out DownloadedAttachment downloaded)
    {
        lock (downloadedAttachmentsLock)
        {
            if (!downloadedAttachments.TryGetValue(key, out var cached))
            {
                downloaded = default!;
                return false;
            }

            downloaded = cached;

            var file = new FileInfo(downloaded.LocalPath);
            if (!file.Exists ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Length != downloaded.SizeBytes ||
                !HasMatchingContentDigest(downloaded.LocalPath, downloaded.ContentDigest))
            {
                downloadedAttachments.Remove(key);
                downloaded = default!;
                return false;
            }

            return true;
        }
    }

    private static AttachmentReference Omitted(ProviderAttachment attachment, string reason) => new(
        attachment.Url.ToString(),
        attachment.SuggestedFileName,
        LocalPath: string.Empty,
        DescribeProvenance(attachment.Source),
        SizeBytes: 0,
        IsOmitted: true,
        OmissionReason: reason);

    private static string DescribeProvenance(AttachmentSource source) =>
        source.ThreadId is null ? $"{source.Surface}:{source.SourceId}" : $"{source.Surface}:{source.SourceId}:{source.ThreadId}";

    /// <summary>Resolves and validates <paramref name="url"/>'s host, returning the full set of
    /// resolved addresses when every one of them is safe, or <see langword="null"/> when any is
    /// unsafe or resolution fails. The caller pins the real download's connection to this exact set
    /// (rather than letting the transport re-resolve independently) to close the DNS-rebinding
    /// TOCTOU window between this check and the actual connect (specification §15).</summary>
    private static async Task<IReadOnlySet<IPAddress>?> ValidateAnonymousDestinationAsync(
        Uri url,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHostAddressesAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await (resolveHostAddressesAsync?.Invoke(url.Host, cancellationToken)
                ?? Dns.GetHostAddressesAsync(url.Host, cancellationToken)).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(IsUnsafeDestination))
            {
                return null;
            }

            return addresses.ToHashSet();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Rejects loopback, private, link-local, CGNAT, benchmarking, and multicast/reserved
    /// address ranges (specification §15: never crawl/fetch internal or cloud-metadata endpoints).
    /// Covers both the originally-defended RFC 1918/link-local ranges and the ranges a bare-IP-
    /// literal or DNS-rebinding attacker can otherwise reach: <c>0.0.0.0</c>/<c>::</c> (this host),
    /// <c>100.64.0.0/10</c> (CGNAT, routes to the host's own network on many cloud providers),
    /// <c>192.0.0.0/24</c> (IETF protocol assignments, includes cloud metadata relay ranges on some
    /// platforms), <c>198.18.0.0/15</c> (benchmarking), IPv6 site-local (<c>fec0::/10</c>),
    /// unspecified/reserved (<c>::/8</c>, <c>100::/64</c>, <c>2001:db8::/32</c>), and multicast
    /// (<c>ff00::/8</c>).</summary>
    private static bool IsUnsafeDestination(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 10 ||
                bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                (bytes[0] == 198 && bytes[1] is 18 or 19),
            AddressFamily.InterNetworkV6 => address.IsIPv6SiteLocal ||
                // Only 2000::/3 is globally-routable IPv6 unicast. Reject every other
                // non-special case as reserved rather than relying on platform-specific
                // IPAddress classifications for deprecated site-local and future ranges.
                (bytes[0] & 0xe0) != 0x20 ||
                bytes[0] == 0 ||
                (bytes[0] & 0xfe) == 0xfc ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00 &&
                 bytes[4] == 0x00 && bytes[5] == 0x00 && bytes[6] == 0x00 && bytes[7] == 0x00) ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) ||
                bytes[0] == 0xff,
            _ => true,
        };
    }

    private static string ComputeContentDigest(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool HasMatchingContentDigest(string path, string? expectedDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest))
        {
            return false;
        }

        try
        {
            return string.Equals(expectedDigest, ComputeContentDigest(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void ProtectRetainedAttachmentDirectory(string destinationDirectory)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(destinationDirectory))
        {
            return;
        }

        File.SetUnixFileMode(
            destinationDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        foreach (var file in Directory.EnumerateFiles(destinationDirectory))
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
    }

    private static void MakeRetainedAttachmentDirectoryWritable(string destinationDirectory)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(destinationDirectory))
        {
            return;
        }

        File.SetUnixFileMode(destinationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool HasValidDownloadedSize(DownloadedAttachment downloaded, long cap)
    {
        if (downloaded.SizeBytes < 0 || downloaded.SizeBytes > cap)
        {
            return false;
        }

        try
        {
            return new FileInfo(downloaded.LocalPath).Length == downloaded.SizeBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void DeletePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}


internal sealed record AttachmentManifestEntry(string Url, string SafeFileName, long SizeBytes, string? ContentDigest);

/// <summary>Mutable running byte total shared across every <see cref="AttachmentPipeline.ProcessAsync"/>
/// call for one workflow context. The total attachment size cap is enforced across the whole context,
/// not per comment body.</summary>
public sealed class RemainingBudget(long totalBytes, long consumedBytes = 0)
{
    private long remaining = Math.Max(0, totalBytes - Math.Clamp(consumedBytes, 0, totalBytes));

    public long Remaining => Volatile.Read(ref remaining);

    public void Consume(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref remaining);
            if (current <= 0)
            {
                return;
            }

            var next = bytes >= current ? 0 : current - bytes;
            if (Interlocked.CompareExchange(ref remaining, next, current) == current)
            {
                return;
            }
        }
    }

}
