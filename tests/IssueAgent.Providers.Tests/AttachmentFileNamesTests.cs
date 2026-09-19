using IssueAgent.Providers;

namespace IssueAgent.Providers.Tests;

public sealed class AttachmentFileNamesTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("/etc/shadow", "shadow")]
    [InlineData("normal-file.png", "normal-file.png")]
    [InlineData("bad:name?.txt", "bad_name_.txt")]
    [InlineData("...", "attachment")]
    public void SanitizeStripsTraversalAndInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, AttachmentFileNames.Sanitize(input));
    }

    [Fact]
    public void ResolveSafeDestinationStaysInsideDestinationDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var resolved = AttachmentFileNames.ResolveSafeDestination(root, "report.pdf");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "report.pdf")), resolved);
    }

    [Fact]
    public void ResolveSafeDestinationRejectsTraversalThatWouldEscapeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        // Even a maliciously crafted absolute suggestion collapses to a bare filename before
        // resolution, so this proves the guard trips only if sanitization is ever bypassed.
        var resolved = AttachmentFileNames.ResolveSafeDestination(root, "../../../etc/passwd");

        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSafeDestinationKeepsSameNamedAttachmentsDistinct()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var first = AttachmentFileNames.ResolveSafeDestination(root, "report.pdf");
            File.WriteAllText(first, "first");

            var second = AttachmentFileNames.ResolveSafeDestination(root, "report.pdf");

            Assert.Equal(Path.Combine(root, "report-1.pdf"), second);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveSafeDestinationRejectsSymbolicLinkDestination()
    {
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "target");
        var link = Path.Combine(parent, "link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                AttachmentFileNames.ResolveSafeDestination(link, "report.pdf"));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(parent, recursive: true);
        }
    }
}
