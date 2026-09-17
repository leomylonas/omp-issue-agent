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
}
