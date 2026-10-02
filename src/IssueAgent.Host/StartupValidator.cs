using System.Runtime.InteropServices;
using System.Text;
using IssueAgent.Configuration;
using IssueAgent.Git;
using IssueAgent.Omp;
using Microsoft.Extensions.Options;

namespace IssueAgent.Host;

/// <summary>Performs global, non-destructive checks before readiness is advertised. Repository
/// connectivity is intentionally isolated: a single provider outage does not make the process
/// unusable.</summary>
public sealed partial class StartupValidator(
    IOptions<IssueAgentOptions> options,
    EffectiveIssueAgentConfiguration effectiveConfiguration,
    ProviderRegistry providers,
    IssueAgent.Git.IGitRepositoryManager git,
    DefaultBranchResolver defaultBranchResolver,
    ILogger<StartupValidator> logger)
{
    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        ValidateWorkspace(configuration.Workspace.RootPath);
        ValidateRequiredTooling(effectiveConfiguration);
        ValidateOmp(configuration.Omp.ExecutablePath);
        try
        {
            await ProbeOmpAsync(configuration, effectiveConfiguration.Omp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            IsTransientOmpStartupDependencyFailure(exception))
        {
            LogOmpValidationWarning(logger, exception);
        }

        foreach (var providerOptions in effectiveConfiguration.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = providers.Get(providerOptions.Name);
            if (providerOptions.Source.IdentityOverride is null)
            {
                try
                {
                    _ = await provider.GetCurrentIdentityAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    LogProviderValidationWarning(logger, exception, provider.Name);
                }
            }
            foreach (var repositoryOptions in providerOptions.Repositories.Where(repository => repository.Enabled))
            {
                try
                {
                    var authentication = providers.GetGitAuthentication(repositoryOptions.Id);
                    await git.EnsureBareRepositoryAsync(repositoryOptions.Id, repositoryOptions.CloneUrl, authentication, cancellationToken).ConfigureAwait(false);
                    await git.FetchAsync(repositoryOptions.Id, authentication, cancellationToken).ConfigureAwait(false);
                    var repositoryRef = new IssueAgent.Providers.RepositoryRef(repositoryOptions.Id, repositoryOptions.OwnerOrNamespace, repositoryOptions.Name);
                    var targetBranch = await defaultBranchResolver
                        .ResolveAsync(provider.Name, repositoryRef, repositoryOptions.TargetBranch, cancellationToken)
                        .ConfigureAwait(false);
                    _ = await git.ResolveBranchCommitAsync(repositoryOptions.Id, targetBranch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    LogRepositoryValidationWarning(logger, provider.Name, repositoryOptions.Id, exception.GetType().Name);
                }
            }
        }
    }

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Repository startup validation failed for {Provider}/{Repository} with {ExceptionType}")]
    private static partial void LogRepositoryValidationWarning(ILogger logger, string provider, string repository, string exceptionType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "OMP startup dependency validation failed; workflow execution will retry at runtime")]
    private static partial void LogOmpValidationWarning(ILogger logger, Exception exception);

    internal static bool IsTransientOmpStartupDependencyFailure(Exception exception) =>
        exception switch
        {
            OmpBrokerUnavailableException or OmpModelUnavailableException => true,
            _ when exception.InnerException is not null => IsTransientOmpStartupDependencyFailure(exception.InnerException),
            _ => false,
        };

    /// <summary>Validates executables needed by the configured local Git transport before any
    /// remote validation is attempted. Remote reachability is retryable; a missing image tool is not.</summary>
    internal static void ValidateRequiredTooling(EffectiveIssueAgentConfiguration configuration) =>
        ValidateRequiredTooling(configuration, IsExecutableAvailable);

    internal static void ValidateRequiredTooling(
        EffectiveIssueAgentConfiguration configuration,
        Func<string, bool> isExecutableAvailable)
    {
        var enabledRepositories = configuration.Providers
            .SelectMany(provider => provider.Repositories)
            .Where(repository => repository.Enabled)
            .ToArray();
        if (enabledRepositories.Length == 0)
        {
            return;
        }

        var requiredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "git",
            "git-lfs",
        };
        foreach (var repository in enabledRepositories.Where(repository =>
                     repository.Git.Mode == ConfiguredGitAuthenticationMode.Ssh))
        {
            requiredTools.Add("ssh");
            if (repository.Git.SshHostVerificationMode == ConfiguredSshHostVerificationMode.Pinned)
            {
                requiredTools.Add("ssh-keyscan");
            }
        }

        ValidateRequiredTools(requiredTools, isExecutableAvailable);
    }

    internal static void ValidateRequiredTools(
        IEnumerable<string> requiredTools,
        Func<string, bool> isExecutableAvailable)
    {
        foreach (var tool in requiredTools)
        {
            if (!isExecutableAvailable(tool))
            {
                throw new InvalidOperationException($"Required local executable '{tool}' was not found or is not executable.");
            }
        }
    }

    private static bool IsExecutableAvailable(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, string.Concat(executable, extension));
                if (File.Exists(candidate) && HasEffectiveExecuteAccess(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }


    internal static void ValidateWorkspace(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException("IssueAgent workspace root path is required.");
        }

        Directory.CreateDirectory(rootPath);
        ProbeDirectoryWriteability(rootPath);
        foreach (var child in new[] { "repos", "workflows", "omp" })
        {
            var path = Path.Combine(rootPath, child);
            Directory.CreateDirectory(path);
            if (string.Equals(child, "omp", StringComparison.Ordinal))
            {
                MakeDirectoryWritableByOmp(path);
            }

            ProbeDirectoryWriteability(path);
        }
    }

    internal static void ProbeDirectoryWriteability(string path)
    {
        var probePath = Path.Combine(path, $".issue-agent-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1,
                       options: FileOptions.WriteThrough))
            {
                stream.WriteByte(0);
            }

            File.Delete(probePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"IssueAgent workspace path '{path}' is not writable.", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void MakeDirectoryWritableByOmp(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path) |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute;
        File.SetUnixFileMode(path, mode);
    }
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Provider identity validation failed for {Provider}; polling will retry at runtime")]
    private static partial void LogProviderValidationWarning(ILogger logger, Exception exception, string provider);

    internal static void ValidateOmp(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException($"Required OMP executable '{executablePath}' was not found.");
        }

        if (!OperatingSystem.IsWindows() && !HasEffectiveExecuteAccess(executablePath))
        {
            throw new InvalidOperationException($"Required OMP executable '{executablePath}' is not executable by the service identity.");
        }
    }

    internal static async Task ProbeOmpAsync(
        IssueAgentOptions options,
        EffectiveOmpConfiguration omp,
        CancellationToken cancellationToken)
    {
        var connection = new Dictionary<string, string>(omp.ConnectionSettings, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(omp.AuthBrokerUrl))
        {
            connection["OMP_AUTH_BROKER_URL"] = omp.AuthBrokerUrl;
        }

        var probeDirectory = Path.Combine(
            options.Workspace.RootPath,
            "workflows",
            Guid.NewGuid().ToString());
        var worktreeDirectory = Path.Combine(probeDirectory, "worktree");
        var sessionDirectory = Path.Combine(probeDirectory, "omp");
        Directory.CreateDirectory(worktreeDirectory);
        Directory.CreateDirectory(sessionDirectory);
        try
        {
            var environment = OmpEnvironment.Build(
                Environment.GetEnvironmentVariables()
                    .Cast<System.Collections.DictionaryEntry>()
                    .ToDictionary(
                        entry => (string)entry.Key,
                        entry => entry.Value as string,
                        StringComparer.Ordinal),
                connection,
                MergeExecutionValues(options.Omp.ExecutionVariables, omp.ExecutionSecrets),
                OmpRuntimeEnvironmentFactory.GetNonOmpSecretSourceNames(options));
            await using var client = OmpProcessClientFactory.Start(
                omp.ExecutablePath,
                ["--mode", "rpc", "--session-dir", sessionDirectory],
                worktreeDirectory,
                environment,
                timeout: omp.Timeout ?? TimeSpan.FromSeconds(10));
            _ = await client.CreateSessionAsync(
                omp.Roles.GetValueOrDefault("planning", "plan"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Required OMP executable '{omp.ExecutablePath}' failed the startup compatibility probe.",
                exception);
        }
        finally
        {
            if (Directory.Exists(probeDirectory))
            {
                Directory.Delete(probeDirectory, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> MergeExecutionValues(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> secrets)
    {
        var values = new Dictionary<string, string>(variables, StringComparer.Ordinal);
        foreach (var (name, value) in secrets)
        {
            values[name] = value;
        }

        return values;
    }

    internal static bool HasEffectiveExecuteAccess(string path) =>
        OperatingSystem.IsWindows() ||
        FAccessAt(CurrentWorkingDirectory, Encoding.UTF8.GetBytes(string.Concat(path, '\0')), ExecuteAccess, EffectiveAccess) == 0;

    [DllImport("libc", EntryPoint = "faccessat", SetLastError = true)]
    private static extern int FAccessAt(int directoryFileDescriptor, byte[] path, int mode, int flags);

    private const int CurrentWorkingDirectory = -100;
    private const int ExecuteAccess = 1;
    private const int EffectiveAccess = 0x200;
}
