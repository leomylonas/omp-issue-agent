using System.Diagnostics;

namespace IssueAgent.Git;

/// <summary>
/// Wraps the pinned <c>git-lfs</c> executable. Never relies on <c>git lfs install</c> or Git hooks;
/// the required <c>filter.lfs.*</c> config is written directly to each worktree's local Git config
/// (see <see cref="LibGit2SharpRepositoryManager"/>), and every invocation runs with an isolated
/// <c>HOME</c>/<c>GIT_CONFIG_NOSYSTEM=1</c> so ambient host Git/LFS config is never inherited.
/// </summary>
public static class GitLfsRunner
{
    public static bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "-c", "core.hooksPath=/dev/null", "lfs", "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True when the worktree's committed <c>.gitattributes</c> declares an LFS filter,
    /// meaning LFS materialization/upload is required rather than optional.</summary>
    public static bool RepositoryRequiresLfs(string worktreePath)
    {
        var attributesPath = Path.Combine(worktreePath, ".gitattributes");
        return File.Exists(attributesPath) &&
            File.ReadAllLines(attributesPath).Any(line => line.Contains("filter=lfs", StringComparison.Ordinal));
    }

    /// <summary>Replaces LFS pointer files in the worktree with their real content.</summary>
    public static async Task MaterializeContentAsync(
        string worktreePath,
        GitAuthentication authentication,
        CancellationToken cancellationToken)
    {
        await EnsureFiltersRegisteredAsync(worktreePath, authentication, cancellationToken).ConfigureAwait(false);
        await RunAsync(worktreePath, authentication, cancellationToken, "lfs", "pull").ConfigureAwait(false);
    }

    /// <summary>Uploads every LFS object referenced by <paramref name="branchName"/> that the remote
    /// does not already have. Must complete successfully before the corresponding <c>git push</c>
    /// publishes the ref; a failure here means the publication failed even if a later plain push
    /// would have succeeded.</summary>
    public static async Task UploadObjectsAsync(
        string worktreePath,
        string branchName,
        GitAuthentication authentication,
        CancellationToken cancellationToken)
    {
        await EnsureFiltersRegisteredAsync(worktreePath, authentication, cancellationToken).ConfigureAwait(false);
        await RunAsync(worktreePath, authentication, cancellationToken, "lfs", "push", "origin", "--", branchName).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers the <c>filter.lfs.*</c> config git-lfs itself expects to see before it will treat
    /// pull/checkout as "opted in" (git-lfs otherwise silently skips checkout with "Git LFS is not
    /// installed for this repository", which it detects independently of the filter config already
    /// set on the worktree). <c>--skip-repo</c> guarantees no hook is written, keeping hooks
    /// disabled everywhere else in this codebase.
    /// </summary>
    private static Task EnsureFiltersRegisteredAsync(
        string worktreePath,
        GitAuthentication authentication,
        CancellationToken cancellationToken) =>
        RunAsync(worktreePath, authentication, cancellationToken, "lfs", "install", "--local", "--skip-repo");

    private static async Task RunAsync(
        string worktreePath,
        GitAuthentication authentication,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        if (!IsAvailable())
        {
            throw new GitLfsUnavailableException("git-lfs is required but is not available in this environment.");
        }

        var isolatedHome = Directory.CreateTempSubdirectory("issueagent-lfs-home-").FullName;
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = worktreePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.hooksPath=/dev/null");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            startInfo.Environment["HOME"] = isolatedHome;
            startInfo.Environment["XDG_CONFIG_HOME"] = isolatedHome;
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "0";

            ApplyAuthentication(startInfo, authentication, isolatedHome);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process for LFS operation.");
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
            }
        }
        finally
        {
            Directory.Delete(isolatedHome, recursive: true);
        }
    }

    private static void ApplyAuthentication(ProcessStartInfo startInfo, GitAuthentication authentication, string isolatedHome)
    {
        switch (authentication.Mode)
        {
            case GitAuthenticationMode.ProviderToken or GitAuthenticationMode.Token:
                var originHost = GitUrlHost.TryGetHost(GetOriginUrl(startInfo.WorkingDirectory));
                var effectiveHost = ResolveLfsEndpointHost(startInfo.WorkingDirectory) ?? originHost;
                if (originHost is null || !string.Equals(effectiveHost, originHost, StringComparison.OrdinalIgnoreCase))
                {
                    // The effective LFS endpoint (possibly overridden by a committed .lfsconfig)
                    // does not match the trusted origin host: never forward credentials
                    // (specification §11 "unknown host → never forward credentials").
                    break;
                }

                var askPassPath = Path.Combine(isolatedHome, "askpass.sh");
                File.WriteAllText(askPassPath, "#!/bin/sh\nprintf '%s\\n' \"$ISSUEAGENT_GIT_TOKEN\"\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(askPassPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                startInfo.Environment["GIT_ASKPASS"] = askPassPath;
                startInfo.Environment["ISSUEAGENT_GIT_TOKEN"] = authentication.HttpsToken!;
                startInfo.Environment["GIT_USERNAME"] = authentication.HttpsUsername ?? "x-access-token";
                break;

            case GitAuthenticationMode.Ssh:
                var trust = authentication.SshTrust ?? throw new InvalidOperationException("SSH transport requires an explicit host-verification policy; none was configured.");
                var remoteUrl = GetOriginUrl(startInfo.WorkingDirectory);
                startInfo.Environment["GIT_SSH_COMMAND"] = GitSshTransport.BuildSshCommandForLfs(authentication, trust, remoteUrl, isolatedHome);
                break;

            case GitAuthenticationMode.Anonymous:
                break;
        }
    }

    private static string GetOriginUrl(string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo("git")
        {
            ArgumentList = { "-c", "core.hooksPath=/dev/null", "-C", workingDirectory, "remote", "get-url", "origin" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start git process.");
        var url = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return url;
    }

    /// <summary>Resolves the LFS endpoint host git-lfs will actually use: a committed
    /// <c>.lfsconfig</c> <c>lfs.url</c> override when present, otherwise <see langword="null"/> (the
    /// caller then trusts the origin remote host instead of forwarding credentials blindly).</summary>
    private static string? ResolveLfsEndpointHost(string workingDirectory)
    {
        var lfsConfigPath = Path.Combine(workingDirectory, ".lfsconfig");
        if (!File.Exists(lfsConfigPath))
        {
            return null;
        }

        using var process = Process.Start(new ProcessStartInfo("git")
        {
            ArgumentList = { "-c", "core.hooksPath=/dev/null", "config", "--file", lfsConfigPath, "--get", "lfs.url" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start git process.");
        var url = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return string.IsNullOrEmpty(url) ? null : GitUrlHost.TryGetHost(url);
    }
}

public sealed class GitLfsUnavailableException(string message) : Exception(message);
