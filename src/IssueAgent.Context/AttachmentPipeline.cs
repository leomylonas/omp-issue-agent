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
public sealed class AttachmentPipeline(IGitProvider provider, AttachmentLimits limits)
{
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

        foreach (var url in MarkdownAttachmentScanner.ScanLinks(body))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isTrusted = provider.IsTrustedAttachmentHost(url);
            if (!isTrusted && !MarkdownAttachmentScanner.IsDirectFileLink(url))
            {
                // Not a provider-owned attachment and not an obvious direct-file link: this is an
                // ordinary webpage reference IssueAgent must not crawl.
                continue;
            }

            var suggestedFileName = Path.GetFileName(url.AbsolutePath) is { Length: > 0 } name ? name : "attachment";
            var providerAttachment = new ProviderAttachment(url, suggestedFileName, SizeBytes: null, source, isTrusted);

            if (remainingBudget.Remaining <= 0)
            {
                results.Add(Omitted(providerAttachment, "Total attachment budget for this workflow context was already exhausted."));
                continue;
            }

            var perAttachmentCap = Math.Min(limits.MaxAttachmentSizeBytes, remainingBudget.Remaining);

            try
            {
                var downloaded = await provider
                    .DownloadAttachmentAsync(providerAttachment, destinationDirectory, perAttachmentCap, cancellationToken)
                    .ConfigureAwait(false);

                remainingBudget.Consume(downloaded.SizeBytes);
                results.Add(new AttachmentReference(
                    url.ToString(),
                    downloaded.SafeFileName,
                    downloaded.LocalPath,
                    DescribeProvenance(source),
                    downloaded.SizeBytes));
            }
            catch (AttachmentTooLargeException)
            {
                results.Add(Omitted(providerAttachment, $"Attachment exceeds the {perAttachmentCap}-byte limit for this download."));
            }
        }

        return results;
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
}

/// <summary>Mutable running total of the per-workflow attachment byte budget, shared across every
/// <see cref="AttachmentPipeline.ProcessAsync"/> call for one workflow context.</summary>
public sealed class RemainingBudget(long totalBytes)
{
    private long remaining = totalBytes;

    public long Remaining => Volatile.Read(ref remaining);

    public void Consume(long bytes) => Interlocked.Add(ref remaining, -bytes);
}
