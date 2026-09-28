using IssueAgent.Host;
using IssueAgent.Providers;
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

    [Fact]
    public void PollingEligibilityDefersOnlyTheRateLimitedRepositoryUntilRetryWindowEnds()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var schedule = new PollingEligibilitySchedule(time);

        schedule.Defer("github/octo/widgets", TimeSpan.FromSeconds(30));

        Assert.False(schedule.IsEligible("github/octo/widgets"));
        Assert.True(schedule.IsEligible("github/octo/other"));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(schedule.IsEligible("github/octo/widgets"));
    }

    [Fact]
    public void PollingEligibilityClampsProviderRetryWindow()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var schedule = new PollingEligibilitySchedule(time);

        schedule.Defer("github/octo/widgets", TimeSpan.MaxValue);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(schedule.IsEligible("github/octo/widgets"));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan elapsed) => current += elapsed;
    }

    [Fact]
    public void PollingRateLimitScopeDefersProviderRetryInsteadOfWaitingInPollWork()
    {
        using var scope = PollingRateLimitScheduling.Enter();

        var exception = Assert.Throws<PollingRateLimitedException>(
            () => PollingRateLimitScheduling.ThrowIfEnabled(TimeSpan.FromMinutes(3)));

        Assert.Equal(TimeSpan.FromMinutes(3), exception.RetryAfter);
    }
}
