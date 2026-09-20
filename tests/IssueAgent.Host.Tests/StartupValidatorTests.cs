using System.Reflection;
using IssueAgent.Configuration;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task ValidateAsyncTreatsAnUnrequestedOperationCancellationAsRepositoryConnectivityFailure()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-startup-{Guid.NewGuid():N}");
        try
        {
            var validator = CreateValidator(workspace, new OperationCanceledException());

            await validator.ValidateAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidateAsyncPropagatesOperationCancellationRequestedByTheCaller()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-startup-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var validator = CreateValidator(workspace, new OperationCanceledException(), cancellation);

            await Assert.ThrowsAsync<OperationCanceledException>(() => validator.ValidateAsync(cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static StartupValidator CreateValidator(
        string workspace,
        OperationCanceledException exception,
        CancellationTokenSource? cancellation = null)
    {
        var source = new ProviderOptions
        {
            Name = "github",
            Kind = ProviderKind.GitHub,
            BaseUri = new Uri("https://api.example.test/"),
            IdentityOverride = "issue-agent",
        };
        var repository = new EffectiveRepositoryConfiguration(
            new RepositoryOptions { Id = "repository", Name = "repository", CloneUrl = "https://git.example.test/team/repository.git" },
            source.Name,
            source.Kind,
            "repository",
            "team",
            "repository",
            "https://git.example.test/team/repository.git",
            "main",
            true,
            DateTimeOffset.MinValue,
            false,
            0,
            1,
            1,
            ConfiguredWorkflowMode.Full,
            [],
            new EffectiveGitConfiguration(
                ConfiguredGitAuthenticationMode.Anonymous,
                null,
                null,
                null,
                null,
                null,
                ConfiguredTlsTrustMode.System,
                [],
                [],
                null,
                []),
            "IssueAgent",
            "issue-agent@example.test",
            null,
            new Dictionary<string, string>());
        var options = new IssueAgentOptions
        {
            Workspace = new WorkspaceOptions { RootPath = workspace },
            Omp = new OmpOptions { ExecutablePath = "/bin/sh" },
        };
        var configuration = new EffectiveIssueAgentConfiguration(
            options,
            [new EffectiveProviderConfiguration(source, source.Name, null, [repository])],
            new EffectiveOmpConfiguration("/bin/sh", null, null, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>()),
            new EffectiveNotificationsConfiguration(ConfiguredTlsTrustMode.System, [], []));
        var registry = new ProviderRegistry(
            configuration,
            new RetryPolicy { InitialDelay = TimeSpan.Zero, MaxJitter = TimeSpan.Zero },
            new IssueAgentMetrics(),
            NullLoggerFactory.Instance);
        var git = DispatchProxy.Create<IGitRepositoryManager, ThrowingGitRepositoryManager>();
        ((ThrowingGitRepositoryManager)(object)git).Exception = exception;
        ((ThrowingGitRepositoryManager)(object)git).Cancellation = cancellation;

        return new StartupValidator(
            Options.Create(options),
            configuration,
            registry,
            git,
            new DefaultBranchResolver(registry),
            NullLogger<StartupValidator>.Instance);
    }

#pragma warning disable CA1852 // DispatchProxy generates a derived type at runtime.
    private class ThrowingGitRepositoryManager : DispatchProxy
    {
        public OperationCanceledException Exception { get; set; } = null!;
        public CancellationTokenSource? Cancellation { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Cancellation?.Cancel();
            throw Exception;
        }
    }
}
#pragma warning restore CA1852
