using System.Net;
using IssueAgent.Providers;

namespace IssueAgent.Context.Tests;

public sealed class AttachmentPipelineTests
{
    private readonly FakeGitProvider provider = new();
    private readonly string destination = Path.Combine(Path.GetTempPath(), "issueagent-attachment-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProcessAsyncDownloadsTrustedAndDirectFileLinks()
    {
        provider.TrustedHosts.Add("github.example");
        provider.DownloadableContent["https://github.example/files/1/log.txt"] = "log contents"u8.ToArray();
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        var budget = new RemainingBudget(100 * 1024 * 1024);

        var results = await pipeline.ProcessAsync(
            "See attached: [log.txt](https://github.example/files/1/log.txt)",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        var attachment = Assert.Single(results);
        Assert.False(attachment.IsOmitted);
        Assert.True(File.Exists(attachment.LocalPath));
    }

    [Fact]
    public async Task ProcessAsyncSkipsOrdinaryWebpageLinksThatAreNeitherTrustedNorDirectFiles()
    {
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        var budget = new RemainingBudget(100 * 1024 * 1024);

        var results = await pipeline.ProcessAsync(
            "See the docs at https://example.com/guide for details.",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ProcessAsyncSkipsRelativeLinksWhenProviderDoesNotResolveThem()
    {
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits());

        var results = await pipeline.ProcessAsync(
            "[guide](/docs/guide.pdf)",
            new AttachmentSource("issue-description", "1"),
            destination,
            new RemainingBudget(100 * 1024 * 1024),
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ProcessAsyncOmitsAttachmentExceedingPerAttachmentLimit()
    {
        provider.DownloadableContent["https://example.com/huge.pdf"] = new byte[10];
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits { MaxAttachmentSizeBytes = 5 });
        var budget = new RemainingBudget(100 * 1024 * 1024);

        var results = await pipeline.ProcessAsync(
            "[huge](https://example.com/huge.pdf)",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        var attachment = Assert.Single(results);
        Assert.True(attachment.IsOmitted);
        Assert.NotNull(attachment.OmissionReason);
    }

    [Fact]
    public async Task ProcessAsyncOmitsRemainingAttachmentsOnceTotalBudgetExhausted()
    {
        provider.DownloadableContent["https://example.com/a.pdf"] = new byte[10];
        provider.DownloadableContent["https://example.com/b.pdf"] = new byte[10];
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        var budget = new RemainingBudget(10);

        var results = await pipeline.ProcessAsync(
            "[a](https://example.com/a.pdf) and [b](https://example.com/b.pdf)",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].IsOmitted);
        Assert.True(results[1].IsOmitted);
    }

    [Fact]
    public async Task ProcessAsyncDecrementsBudgetByActualDownloadedBytes()
    {
        provider.DownloadableContent["https://example.com/a.pdf"] = new byte[42];
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        var budget = new RemainingBudget(1000);

        await pipeline.ProcessAsync(
            "[a](https://example.com/a.pdf)",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        Assert.Equal(1000 - 42, budget.Remaining);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("::")]
    [InlineData("ff02::1")]
    public async Task ProcessAsyncOmitsDirectFileLinkResolvingToUnsafeAddress(string resolvedAddress)
    {
        var pipeline = new AttachmentPipeline(
            provider,
            new AttachmentLimits(),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(resolvedAddress) }));
        var budget = new RemainingBudget(100 * 1024 * 1024);

        var results = await pipeline.ProcessAsync(
            "[metadata](https://untrusted.example/metadata.json)",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        var attachment = Assert.Single(results);
        Assert.True(attachment.IsOmitted);
        Assert.Contains("unsafe network address", attachment.OmissionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsyncOmitsRatherThanCrashesOnADownloadTransportFailureSuchAsABlockedRedirect()
    {
        // Regression: the anonymous attachment client disables AllowAutoRedirect so an unvalidated
        // 3xx to an internal/loopback/cloud-metadata endpoint is never followed (specification §15).
        // A blocked redirect surfaces as HttpRequestException from EnsureSuccessStatusCode; one bad
        // link must be omitted, not crash the whole ProcessAsync call for every other link.
        provider.TrustedHosts.Add("github.example");
        provider.AttachmentDownloadOverride = (_, _, _) => throw new HttpRequestException("Response status code does not indicate success: 302 (Found).", null, System.Net.HttpStatusCode.Found);

        var results = await RunProcessAsync("[redirect](https://github.example/files/redirect.pdf)");

        var attachment = Assert.Single(results);
        Assert.True(attachment.IsOmitted);
        Assert.Contains("download failed", attachment.OmissionReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsyncOmitsAndDeletesPartialFileWhenFinalDownloadFailureIsNonCallerCancellationOrStreamingIo(bool isCancellation)
    {
        provider.TrustedHosts.Add("github.example");
        var partialPath = Path.Combine(destination, "partial.pdf");
        provider.AttachmentDownloadOverride = (_, _, _) =>
        {
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(partialPath, [1]);
            throw isCancellation
                ? new OperationCanceledException("The download timed out.")
                : new IOException("The response stream ended unexpectedly.");
        };

        var results = await RunProcessAsync("[partial](https://github.example/files/partial.pdf)");

        var attachment = Assert.Single(results);
        Assert.True(attachment.IsOmitted);
        Assert.Contains("download failed", attachment.OmissionReason, StringComparison.Ordinal);
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task ProcessAsyncPropagatesCallerCancellationWhileDeletingPartialFile()
    {
        provider.TrustedHosts.Add("github.example");
        var partialPath = Path.Combine(destination, "cancelled.pdf");
        using var cancellation = new CancellationTokenSource();
        provider.AttachmentDownloadOverride = (_, _, _) =>
        {
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(partialPath, [1]);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ProcessAsync(
            "[cancelled](https://github.example/files/cancelled.pdf)",
            new AttachmentSource("issue-description", "1"),
            destination,
            new RemainingBudget(100 * 1024 * 1024),
            cancellation.Token));

        Assert.False(File.Exists(partialPath));
    }

    private async Task<IReadOnlyList<IssueAgent.Domain.AttachmentReference>> RunProcessAsync(string body)
    {
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits());
        var budget = new RemainingBudget(100 * 1024 * 1024);
        return await pipeline.ProcessAsync(body, new AttachmentSource("issue-description", "1"), destination, budget, CancellationToken.None);
    }

    [Fact]
    public async Task ProcessAsyncOmitsLinksBeyondAttachmentCountLimit()
    {
        provider.TrustedHosts.Add("github.example");
        provider.DownloadableContent["https://github.example/files/a.pdf"] = [1];
        provider.DownloadableContent["https://github.example/files/b.pdf"] = [2];
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits { MaxAttachmentCount = 2 });
        var budget = new RemainingBudget(100 * 1024 * 1024);

        var results = await pipeline.ProcessAsync(
            "[a](https://github.example/files/a.pdf) [b](https://github.example/files/b.pdf) [c](https://github.example/files/c.pdf)",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results.Take(2), attachment => Assert.False(attachment.IsOmitted));
        Assert.True(results[2].IsOmitted);
        Assert.Contains("count exceeds", results[2].OmissionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsyncEnforcesTheAttachmentCountLimitAcrossMultipleCallsSharingOneBudget()
    {
        // Regression: the count limit must bound the whole workflow context (every comment body),
        // not reset per ProcessAsync call — otherwise many comments each carrying one link drive
        // unbounded downloads for a single planning turn (specification §15).
        provider.TrustedHosts.Add("github.example");
        for (var i = 0; i < 5; i++)
        {
            provider.DownloadableContent[$"https://github.example/files/{i}.pdf"] = [1];
        }

        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits { MaxAttachmentCount = 2 });
        var budget = new RemainingBudget(100 * 1024 * 1024);

        var allResults = new List<IssueAgent.Domain.AttachmentReference>();
        for (var i = 0; i < 5; i++)
        {
            allResults.AddRange(await pipeline.ProcessAsync(
                $"[link](https://github.example/files/{i}.pdf)",
                new AttachmentSource("issue-comment", i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                destination,
                budget,
                CancellationToken.None));
        }

        Assert.Equal(5, allResults.Count);
        Assert.Equal(2, allResults.Count(attachment => !attachment.IsOmitted));
        Assert.Equal(3, allResults.Count(attachment => attachment.IsOmitted));
    }

    [Fact]
    public async Task ProcessAsyncOmitsDownloadWhoseActualSizeExceedsRequestedCap()
    {
        provider.TrustedHosts.Add("github.example");
        var partialPath = Path.Combine(destination, "oversized.pdf");
        provider.AttachmentDownloadOverride = (_, _, _) =>
        {
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(partialPath, new byte[6]);
            return new DownloadedAttachment(partialPath, "oversized.pdf", 6);
        };
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits { MaxAttachmentSizeBytes = 5 });
        var budget = new RemainingBudget(5);

        var results = await pipeline.ProcessAsync(
            "[oversized](https://github.example/files/oversized.pdf)",
            new AttachmentSource("issue-description", "1"),
            destination,
            budget,
            CancellationToken.None);

        var attachment = Assert.Single(results);
        Assert.True(attachment.IsOmitted);
        Assert.Equal(5, budget.Remaining);
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task ProcessAsyncDoesNotReturnARestoredAttachmentExceedingTheCurrentPerFileLimit()
    {
        const string url = "https://example.com/oversized.pdf";
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "oversized.pdf"), new byte[6]);
        WriteManifest((url, "oversized.pdf", 6));
        var downloads = 0;
        provider.AttachmentDownloadOverride = (attachment, directory, _) =>
        {
            downloads++;
            var path = AttachmentFileNames.ResolveSafeDestination(directory, attachment.SuggestedFileName);
            File.WriteAllBytes(path, [1]);
            return new DownloadedAttachment(path, Path.GetFileName(path), 1);
        };
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits
        {
            MaxAttachmentSizeBytes = 5,
            MaxTotalSizeBytes = 10,
            MaxAttachmentCount = 2,
        });

        var result = await pipeline.ProcessAsync(
            $"[attachment]({url})",
            new AttachmentSource("issue-description", "1"),
            destination,
            pipeline.CreateRemainingBudget(destination),
            CancellationToken.None);

        var attachment = Assert.Single(result);
        Assert.False(attachment.IsOmitted);
        Assert.Equal(1, attachment.SizeBytes);
        Assert.Equal(1, downloads);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotReturnARestoredAttachmentExceedingTheCurrentTotalLimit()
    {
        const string firstUrl = "https://example.com/a.pdf";
        const string secondUrl = "https://example.com/b.pdf";
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "a.pdf"), new byte[4]);
        File.WriteAllBytes(Path.Combine(destination, "b.pdf"), new byte[4]);
        WriteManifest((firstUrl, "a.pdf", 4), (secondUrl, "b.pdf", 4));
        var downloads = 0;
        provider.AttachmentDownloadOverride = (attachment, directory, _) =>
        {
            downloads++;
            var path = AttachmentFileNames.ResolveSafeDestination(directory, attachment.SuggestedFileName);
            File.WriteAllBytes(path, [1]);
            return new DownloadedAttachment(path, Path.GetFileName(path), 1);
        };
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits
        {
            MaxAttachmentSizeBytes = 10,
            MaxTotalSizeBytes = 6,
            MaxAttachmentCount = 2,
        });

        var result = await pipeline.ProcessAsync(
            $"[attachment]({secondUrl})",
            new AttachmentSource("issue-description", "1"),
            destination,
            pipeline.CreateRemainingBudget(destination),
            CancellationToken.None);

        var attachment = Assert.Single(result);
        Assert.False(attachment.IsOmitted);
        Assert.Equal(1, attachment.SizeBytes);
        Assert.Equal(1, downloads);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotReturnARestoredAttachmentExceedingTheCurrentCountLimit()
    {
        const string firstUrl = "https://example.com/a.pdf";
        const string secondUrl = "https://example.com/b.pdf";
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "a.pdf"), [1]);
        File.WriteAllBytes(Path.Combine(destination, "b.pdf"), [1]);
        WriteManifest((firstUrl, "a.pdf", 1), (secondUrl, "b.pdf", 1));
        var pipeline = new AttachmentPipeline(provider, new AttachmentLimits
        {
            MaxAttachmentSizeBytes = 10,
            MaxTotalSizeBytes = 10,
            MaxAttachmentCount = 1,
        });

        var result = await pipeline.ProcessAsync(
            $"[attachment]({secondUrl})",
            new AttachmentSource("issue-description", "1"),
            destination,
            pipeline.CreateRemainingBudget(destination),
            CancellationToken.None);

        var attachment = Assert.Single(result);
        Assert.True(attachment.IsOmitted);
        Assert.Contains("count exceeds", attachment.OmissionReason, StringComparison.Ordinal);
    }

    private void WriteManifest(params (string Url, string SafeFileName, long SizeBytes)[] entries)
    {
        File.WriteAllText(
            Path.Combine(destination, ".issue-agent-attachment-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(entries.Select(entry => new
            {
                entry.Url,
                entry.SafeFileName,
                entry.SizeBytes,
            })));
    }
}
