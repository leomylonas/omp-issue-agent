using IssueAgent.Providers;

namespace IssueAgent.Providers.Tests;

public sealed class MarkdownAttachmentScannerTests
{
    [Fact]
    public void ScanLinksFindsMarkdownImageAndLinkSyntax()
    {
        const string body = """
            See the crash: ![screenshot](https://user-images.githubusercontent.com/1/a.png)

            Full log attached: [crash.log](https://github.com/example/repo/files/1/crash.log)
            """;

        var links = MarkdownAttachmentScanner.ScanLinks(body).Select(uri => uri.ToString()).ToArray();

        Assert.Contains("https://user-images.githubusercontent.com/1/a.png", links);
        Assert.Contains("https://github.com/example/repo/files/1/crash.log", links);
    }

    [Fact]
    public void ScanLinksFindsBareHttpUrlsOutsideMarkdownSyntax()
    {
        const string body = "Direct file: https://example.com/reports/output.csv please review";

        var links = MarkdownAttachmentScanner.ScanLinks(body).Select(uri => uri.ToString()).ToArray();

        Assert.Contains("https://example.com/reports/output.csv", links);
    }

    [Fact]
    public void ScanLinksIgnoresNonHttpSchemes()
    {
        const string body = "Local path [file](file:///etc/passwd) and mailto:[email](mailto:a@example.com)";

        var links = MarkdownAttachmentScanner.ScanLinks(body).ToArray();

        Assert.Empty(links);
    }

    [Theory]
    [InlineData("https://example.com/report.pdf", true)]
    [InlineData("https://example.com/report.PDF", true)]
    [InlineData("https://example.com/page", false)]
    [InlineData("https://example.com/report.pdf/comment/1", false)]
    public void IsDirectFileLinkMatchesKnownExtensionsOnly(string url, bool expected)
    {
        Assert.Equal(expected, MarkdownAttachmentScanner.IsDirectFileLink(new Uri(url)));
    }
}
