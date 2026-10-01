using System.Reflection;
using IssueAgent.Configuration;
using IssueAgent.Domain;
using IssueAgent.Git;
using IssueAgent.Observability;
using IssueAgent.Omp;
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
    public void ValidateWorkspaceProbesEveryRequiredWritableDirectoryAndCleansUpProbeFiles()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-workspace-{Guid.NewGuid():N}");
        try
        {
            StartupValidator.ValidateWorkspace(workspace);

            Assert.All(
                new[] { workspace, Path.Combine(workspace, "repos"), Path.Combine(workspace, "workflows"), Path.Combine(workspace, "omp") },
                path => Assert.Empty(Directory.EnumerateFiles(path, ".issue-agent-write-probe-*")));
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
    public void ValidateWorkspaceRejectsARequiredDirectoryThatCannotBeWritten()
    {
        if (!OperatingSystem.IsLinux() || string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-workspace-{Guid.NewGuid():N}");
        var repos = Path.Combine(workspace, "repos");
        try
        {
            StartupValidator.ValidateWorkspace(workspace);
            File.SetUnixFileMode(repos, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var exception = Assert.Throws<InvalidOperationException>(() => StartupValidator.ValidateWorkspace(workspace));

            Assert.Contains(repos, exception.Message, StringComparison.Ordinal);
            Assert.Contains("not writable", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(repos))
            {
                File.SetUnixFileMode(repos, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
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
    public async Task ProbeOmpAsyncUsesConfiguredExecutionEnvironmentAndRpcProtocol()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-startup-{Guid.NewGuid():N}");
        try
        {
            var executable = CreateProbeExecutable(workspace);
            var options = new IssueAgentOptions
            {
                Workspace = new WorkspaceOptions { RootPath = workspace },
                Omp = new OmpOptions
                {
                    ExecutablePath = executable,
                    ExecutionVariables = new Dictionary<string, string> { ["PROBE_CONFIGURATION"] = "configured" },
                },
            };
            var omp = new EffectiveOmpConfiguration(
                executable, TimeSpan.FromSeconds(2), null,
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                new Dictionary<string, string> { ["planning"] = "plan" });

            await StartupValidator.ProbeOmpAsync(options, omp, CancellationToken.None);

            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspace, "omp"), "startup-probe-*"));
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
    public async Task ProbeOmpAsyncDoesNotForwardProviderSecretsFromAllowedAmbientNames()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-startup-{Guid.NewGuid():N}");
        var originalConfigFiles = Environment.GetEnvironmentVariable("PI_CONFIG_FILES");
        try
        {
            Environment.SetEnvironmentVariable("PI_CONFIG_FILES", "provider-secret");
            var executable = CreateProbeExecutable(workspace, excludedEnvironmentVariable: "PI_CONFIG_FILES");
            var options = new IssueAgentOptions
            {
                Workspace = new WorkspaceOptions { RootPath = workspace },
                Omp = new OmpOptions
                {
                    ExecutablePath = executable,
                    ExecutionVariables = new Dictionary<string, string> { ["PROBE_CONFIGURATION"] = "configured" },
                },
                Providers =
                [
                    new ProviderOptions
                    {
                        Name = "github",
                        Kind = ProviderKind.GitHub,
                        BaseUri = new Uri("https://api.github.com/"),
                        Token = new SecretSource { Env = "PI_CONFIG_FILES" },
                    },
                ],
            };
            var omp = new EffectiveOmpConfiguration(
                executable, TimeSpan.FromSeconds(2), null,
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                new Dictionary<string, string> { ["planning"] = "plan" });

            await StartupValidator.ProbeOmpAsync(options, omp, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_CONFIG_FILES", originalConfigFiles);
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("""{"type":"ready","protocolVersion":2,"supportedProtocolVersions":[1,2]}""")]
    [InlineData("""{"type":"ready","protocolVersion":1,"supportedProtocolVersions":[2]}""")]
    public async Task ProbeOmpAsyncRejectsAnIncompatibleReadyProtocol(string readyFrame)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-startup-{Guid.NewGuid():N}");
        try
        {
            var executable = CreateProbeExecutable(workspace, readyFrame);
            var options = new IssueAgentOptions
            {
                Workspace = new WorkspaceOptions { RootPath = workspace },
                Omp = new OmpOptions
                {
                    ExecutablePath = executable,
                    ExecutionVariables = new Dictionary<string, string> { ["PROBE_CONFIGURATION"] = "configured" },
                },
            };
            var omp = new EffectiveOmpConfiguration(
                executable, TimeSpan.FromSeconds(2), null,
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                new Dictionary<string, string> { ["planning"] = "plan" });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StartupValidator.ProbeOmpAsync(options, omp, CancellationToken.None));

            Assert.Contains("startup compatibility probe", exception.Message, StringComparison.Ordinal);
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
    public void OmpProtocolSessionAndGenericCommandRejectionsAreFatalAtStartup()
    {
        Assert.False(StartupValidator.IsTransientOmpStartupDependencyFailure(
            new OmpRpcException("OMP resumed a different durable session.")));
        Assert.False(StartupValidator.IsTransientOmpStartupDependencyFailure(
            new InvalidOperationException("OMP ready frame must declare a supported protocol.", new OmpRpcException("protocol mismatch"))));
        Assert.False(StartupValidator.IsTransientOmpStartupDependencyFailure(
            new OmpRemoteException("unknown command new_session")));
        Assert.False(StartupValidator.IsTransientOmpStartupDependencyFailure(
            new TimeoutException("OMP command timed out.")));
    }

    [Fact]
    public void OmpTypedBrokerOrModelDependencyFailuresRemainNonfatalAtStartup()
    {
        Assert.True(StartupValidator.IsTransientOmpStartupDependencyFailure(
            new OmpBrokerUnavailableException("broker temporarily unavailable")));
        Assert.True(StartupValidator.IsTransientOmpStartupDependencyFailure(
            new InvalidOperationException("probe failed", new OmpModelUnavailableException("model unavailable"))));
    }

    [Fact]
    public async Task ProbeOmpAsyncRejectsAProgramThatDoesNotSpeakOmpRpc()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-startup-{Guid.NewGuid():N}");
        try
        {
            var options = new IssueAgentOptions
            {
                Workspace = new WorkspaceOptions { RootPath = workspace },
                Omp = new OmpOptions { ExecutablePath = "/bin/true" },
            };
            var omp = new EffectiveOmpConfiguration(
                "/bin/true", TimeSpan.FromSeconds(2), null,
                new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>());

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => StartupValidator.ProbeOmpAsync(options, omp, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
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

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [Fact]
    public async Task ValidateAsyncTreatsAMissingRepositoryBranchAsAnIsolatedRepositoryFailure()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"issue-agent-startup-{Guid.NewGuid():N}");
        try
        {
            var validator = CreateValidator(workspace, new GitReferenceNotFoundException("main was not found"));

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

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
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

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static StartupValidator CreateValidator(
        string workspace,
        Exception exception,
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
        Directory.CreateDirectory(workspace);
        var executable = CreateProbeExecutable(workspace);
        var options = new IssueAgentOptions
        {
            Workspace = new WorkspaceOptions { RootPath = workspace },
            Omp = new OmpOptions
            {
                ExecutablePath = executable,
                ExecutionVariables = new Dictionary<string, string> { ["PROBE_CONFIGURATION"] = "configured" },
            },
        };
        var configuration = new EffectiveIssueAgentConfiguration(
            options,
            [new EffectiveProviderConfiguration(source, source.Name, null, [repository])],
            new EffectiveOmpConfiguration(executable, TimeSpan.FromSeconds(2), null, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>()),
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

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string CreateProbeExecutable(
        string workspace,
        string readyFrame = """{"type":"ready","protocolVersion":1,"supportedProtocolVersions":[1,2]}""",
        string? excludedEnvironmentVariable = null)
    {
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "fake-omp.sh");
        File.WriteAllText(path, """
            #!/bin/sh
            session_dir=
            previous=
            for argument in "$@"; do
              if [ "$previous" = "--session-dir" ]; then session_dir="$argument"; fi
              previous="$argument"
            done
            [ "$PROBE_CONFIGURATION" = "configured" ] || exit 2
            __EXCLUDED_ENVIRONMENT_CHECK__
            printf '%s\n' '__READY_FRAME__'
            while IFS= read -r request; do
              case "$request" in
                *new_session*) printf '%s\n' '{"type":"response","id":"1","success":true}' ;;
                *get_state*) printf '%s\n' "{\"type\":\"response\",\"id\":\"2\",\"success\":true,\"data\":{\"sessionId\":\"probe\",\"sessionPath\":\"$session_dir/session.jsonl\"}}" ;;
              esac
            done
            """
            .Replace("__READY_FRAME__", readyFrame, StringComparison.Ordinal)
            .Replace(
                "__EXCLUDED_ENVIRONMENT_CHECK__",
                excludedEnvironmentVariable is null ? string.Empty : $"[ -z \"${{{excludedEnvironmentVariable}+x}}\" ] || exit 3",
                StringComparison.Ordinal));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

#pragma warning disable CA1852 // DispatchProxy generates a derived type at runtime.
    private class ThrowingGitRepositoryManager : DispatchProxy
    {
        public Exception Exception { get; set; } = null!;
        public CancellationTokenSource? Cancellation { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Cancellation?.Cancel();
            throw Exception;
        }
    }
}
#pragma warning restore CA1852
