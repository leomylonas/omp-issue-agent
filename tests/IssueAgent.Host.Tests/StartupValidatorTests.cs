using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class StartupValidatorTests
{
    [Fact]
    public void ValidateOmpAcceptsExecutableAvailableThroughGroupOrOtherPermissions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string executablePath = "/bin/sh";
        var mode = File.GetUnixFileMode(executablePath);
        Assert.True((mode & (UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

        StartupValidator.ValidateOmp(executablePath);
    }

    [Fact]
    public void HasEffectiveExecuteAccessRejectsRootOwnedMode0700PathForNonRootService()
    {
        if (!OperatingSystem.IsLinux() || string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            return;
        }

        const string rootHome = "/root";
        var mode = File.GetUnixFileMode(rootHome);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));

        Assert.False(StartupValidator.HasEffectiveExecuteAccess(rootHome));
    }

    [Fact]
    public void ValidateOmpRejectsAnInaccessibleExecutable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"issue-agent-omp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "not executable");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var exception = Assert.Throws<InvalidOperationException>(() => StartupValidator.ValidateOmp(path));

            Assert.Contains("not executable by the service identity", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
