using IssueAgent.Host;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class PollingSchedulerTests
{
    [Fact]
    public void MeasureWorkspaceBytesSkipsReparsePointFilesAndDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"issue-agent-workspace-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(workspace, "workspace-file"), new byte[3]);
            var outsideFile = Path.Combine(outside, "outside-file");
            File.WriteAllBytes(outsideFile, new byte[5]);
            File.CreateSymbolicLink(Path.Combine(workspace, "outside-file-link"), outsideFile);
            Directory.CreateSymbolicLink(Path.Combine(workspace, "outside-directory-link"), outside);

            var measuredBytes = PollingScheduler.MeasureWorkspaceBytes(workspace);

            Assert.Equal(3, measuredBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
