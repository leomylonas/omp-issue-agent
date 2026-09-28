using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Context;

public sealed record AttachmentLimits
{
    public long MaxAttachmentSizeBytes { get; init; } = 25 * 1024 * 1024;

    public long MaxTotalSizeBytes { get; init; } = 100 * 1024 * 1024;

    public int MaxAttachmentCount { get; init; } = 50;
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
    private readonly Dictionary<string, DownloadedAttachment> downloadedAttachments = new(StringComparer.Ordinal);
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

        var results = new List<AttachmentReference>();

        string CacheKey(Uri url) => Path.Combine(destinationDirectory, url.AbsoluteUri);
        foreach (var candidateUrl in MarkdownAttachmentScanner.ScanLinks(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = provider.ResolveAttachmentUrl(candidateUrl);
            if (url is null)
            {
                continue;
            }

            var isTrusted = provider.IsTrustedAttachmentHost(url);
            if (!isTrusted && !MarkdownAttachmentScanner.IsDirectFileLink(url))
            {
                // Not a provider-owned attachment and not an obvious direct-file link: this is an
                // ordinary webpage reference IssueAgent must not crawl.
                continue;
            }

            var suggestedFileName = Path.GetFileName(url.AbsolutePath) is { Length: > 0 } name ? name : "attachment";
            var providerAttachment = new ProviderAttachment(url, suggestedFileName, SizeBytes: null, source, isTrusted);

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
            if (!remainingBudget.TryReserveAttachmentSlot(limits.MaxAttachmentCount))
            {
                results.Add(Omitted(providerAttachment, $"Attachment count exceeds the {limits.MaxAttachmentCount}-attachment limit for this workflow context."));
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

                remainingBudget.Consume(downloaded.SizeBytes);
                CacheDownloadedAttachment(CacheKey(url), downloaded);
                results.Add(new AttachmentReference(
                    url.ToString(),
                    downloaded.SafeFileName,
                    downloaded.LocalPath,
                    DescribeProvenance(source),
                    downloaded.SizeBytes));
            }
            catch (AttachmentTooLargeException)
            {
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, $"Attachment exceeds the {perAttachmentCap}-byte limit for this download."));
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
                // Includes a blocked redirect (AllowAutoRedirect is disabled for the anonymous
                // attachment client precisely so an unvalidated 3xx to an internal/loopback/cloud-
                // metadata endpoint is never followed — specification §15) and any other transport
                // failure. Omitted, not fatal: one bad link must not abort the whole context.
                DeletePartialFile(partialPath);
                results.Add(Omitted(providerAttachment, $"Attachment download failed: {exception.StatusCode?.ToString() ?? exception.GetType().Name}."));
            }
        }

        return results;
    }

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
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != downloaded.SizeBytes)
            {
                downloadedAttachments.Remove(key);
                downloaded = default!;
                return false;
            }

            return true;
        }
    }

    private void CacheDownloadedAttachment(string key, DownloadedAttachment downloaded)
    {
        lock (downloadedAttachmentsLock)
        {
            downloadedAttachments[key] = downloaded;
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
    /// platforms), <c>198.18.0.0/15</c> (benchmarking), and IPv6 multicast (<c>ff00::/8</c>).</summary>
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
            AddressFamily.InterNetworkV6 => (bytes[0] & 0xfe) == 0xfc ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                bytes[0] == 0xff,
            _ => true,
        };
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

/// <summary>Mutable running totals shared across every <see cref="AttachmentPipeline.ProcessAsync"/>
/// call for one workflow context: the remaining byte budget and the remaining attachment-count
/// budget (specification §15). Both are enforced across the whole context, not per comment body.</summary>
public sealed class RemainingBudget(long totalBytes)
{
    private long remaining = Math.Max(0, totalBytes);
    private int attachmentCount;

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

    /// <summary>Atomically reserves one attachment slot against <paramref name="maxAttachmentCount"/>,
    /// shared across every <see cref="AttachmentPipeline.ProcessAsync"/> call for this workflow
    /// context. Returns <see langword="false"/> once the cap is already reached.</summary>
    public bool TryReserveAttachmentSlot(int maxAttachmentCount)
    {
        while (true)
        {
            var current = Volatile.Read(ref attachmentCount);
            if (current >= maxAttachmentCount)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref attachmentCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }
}
