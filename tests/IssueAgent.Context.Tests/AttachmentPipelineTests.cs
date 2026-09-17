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
}
