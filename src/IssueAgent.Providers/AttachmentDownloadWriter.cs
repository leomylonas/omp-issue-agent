namespace IssueAgent.Providers;

public static class AttachmentDownloadWriter
{
    public static async Task<long> WriteAsync(
        HttpContent content,
        string destinationPath,
        long maxSizeBytes,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
                if (totalRead > maxSizeBytes)
                {
                    throw new AttachmentTooLargeException(
                        $"Attachment '{displayName}' exceeded the {maxSizeBytes}-byte limit while streaming.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return totalRead;
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }
}
