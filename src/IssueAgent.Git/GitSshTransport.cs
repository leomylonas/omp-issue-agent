using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IssueAgent.Git;

/// <summary>
/// SSH Git transport implemented via the <c>ssh</c>/<c>git</c> executables.
///
/// The LibGit2Sharp native binaries used by this build have no libssh2 support and expose no SSH
/// credential type, so SSH remote operations cannot go through LibGit2Sharp at all in this
/// environment. This is the documented, intentional fallback for <see cref="GitAuthenticationMode.Ssh"/>;
/// every other transport uses LibGit2Sharp.
///
/// Host verification never trusts an unseen key: it runs <c>ssh-keyscan</c>, computes each offered
/// key's SHA-256 fingerprint, and accepts only keys matching a configured pinned fingerprint before
/// writing them to an ephemeral, process-scoped <c>known_hosts</c> file. <see cref="SshHostVerificationMode.None"/>
/// is honored as an explicit, intentional opt-out (<c>StrictHostKeyChecking=no</c>); a missing
/// <see cref="GitAuthentication.SshTrust"/> is a fatal configuration error, matching the spec's SSH
/// host-verification requirement.
/// </summary>
public sealed partial class GitSshTransport
{
    public static async ValueTask CloneBareAsync(string cloneUrl, string destinationPath, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        await RunGitAsync(["clone", "--bare", "--", cloneUrl, destinationPath], workingDirectory: null, cloneUrl, authentication, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(
            ["config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"],
            destinationPath,
            cloneUrl,
            authentication,
            cancellationToken).ConfigureAwait(false);
        await RunGitAsync(
            ["fetch", "origin", "--prune"],
            destinationPath,
            cloneUrl,
            authentication,
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask FetchAsync(string bareRepositoryPath, GitAuthentication authentication, CancellationToken cancellationToken) =>
        await RunGitAsync(["fetch", "--all", "--prune"], bareRepositoryPath, await GetOriginUrlAsync(bareRepositoryPath, cancellationToken).ConfigureAwait(false), authentication, cancellationToken).ConfigureAwait(false);

    public static async ValueTask PushAsync(string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) =>
        await RunGitAsync(["push", "origin", "--", $"{branchName}:{branchName}"], worktreePath, await GetOriginUrlAsync(worktreePath, cancellationToken).ConfigureAwait(false), authentication, cancellationToken).ConfigureAwait(false);

    /// <summary>Publishes a detached submodule commit to the agent branch without consulting
    /// mutable submodule remote configuration.</summary>
    public static ValueTask PushCommitAsync(string worktreePath, string authoritativeRemoteUrl, string commitSha, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) =>
        RunGitAsync(["push", authoritativeRemoteUrl, "--", $"{commitSha}:refs/heads/{branchName}"], worktreePath, authoritativeRemoteUrl, authentication, cancellationToken);

    /// <summary>Initializes and fetches one submodule through the Git/SSH transport, overriding
    /// the mutable local submodule remote with the committed, resolved authoritative URL.</summary>
    public static ValueTask UpdateSubmoduleAsync(string repositoryPath, string submoduleName, string submodulePath, string authoritativeRemoteUrl, GitAuthentication authentication, CancellationToken cancellationToken) =>
        RunGitAsync(["-c", $"submodule.{submoduleName}.url={authoritativeRemoteUrl}", "submodule", "update", "--init", "--", submodulePath], repositoryPath, authoritativeRemoteUrl, authentication, cancellationToken);

    private static async ValueTask<string> GetOriginUrlAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var isolatedHome = Directory.CreateTempSubdirectory("issueagent-git-home-").FullName;
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                ArgumentList = { "-c", "core.hooksPath=/dev/null", "-C", workingDirectory, "remote", "get-url", "origin" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            ConfigureIsolatedEnvironment(startInfo, isolatedHome);

            var result = await RunProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Could not resolve the origin remote: {result.StandardError}");
            }

            return result.StandardOutput.Trim();
        }
        finally
        {
            Directory.Delete(isolatedHome, recursive: true);
        }
    }

    private static async ValueTask RunGitAsync(IReadOnlyList<string> arguments, string? workingDirectory, string remoteUrl, GitAuthentication authentication, CancellationToken cancellationToken)
    {
        if (authentication.Mode != GitAuthenticationMode.Ssh)
        {
            throw new InvalidOperationException("GitSshTransport only supports SSH authentication.");
        }

        var trust = authentication.SshTrust
            ?? throw new InvalidOperationException("SSH transport requires an explicit host-verification policy; none was configured.");

        var isolatedHome = Directory.CreateTempSubdirectory("issueagent-git-home-").FullName;
        try
        {
            var sshCommand = await BuildSshCommandAsync(authentication, trust, remoteUrl, isolatedHome, cancellationToken).ConfigureAwait(false);
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
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

            ConfigureIsolatedEnvironment(startInfo, isolatedHome);
            startInfo.Environment["GIT_SSH_COMMAND"] = sshCommand;
            ConfigurePassphraseAskPass(startInfo, authentication, isolatedHome);

            var result = await RunProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError}");
            }
        }
        finally
        {
            Directory.Delete(isolatedHome, recursive: true);
        }
    }

    public static ValueTask<string> BuildSshCommandForLfsAsync(GitAuthentication authentication, SshTrust trust, string remoteUrl, string isolatedHome, CancellationToken cancellationToken) =>
        BuildSshCommandAsync(authentication, trust, remoteUrl, isolatedHome, cancellationToken);

    private static async ValueTask<string> BuildSshCommandAsync(GitAuthentication authentication, SshTrust trust, string remoteUrl, string isolatedHome, CancellationToken cancellationToken)
    {
        var keyPath = Path.Combine(isolatedHome, "id_agent");
        File.WriteAllText(keyPath, authentication.SshPrivateKey ?? throw new InvalidOperationException("SSH authentication requires a private key."));
        MakeKeyFilePrivate(keyPath);

        // The passphrase remains only in the child environment and its askpass helper is scoped to
        // this isolated HOME. OpenSSH is forced to invoke it, so no terminal or ambient agent can
        // receive the secret.

        var options = new StringBuilder("ssh -i ").Append(Quote(keyPath)).Append(" -o IdentitiesOnly=yes");
        if (!string.IsNullOrWhiteSpace(authentication.SshUsername))
        {
            // The provider configuration, rather than a mutable remote URL, owns the SSH identity.
            // This also applies to submodule and LFS SSH commands.
            options.Append(" -o User=").Append(Quote(authentication.SshUsername));
        }

        if (trust.Mode == SshHostVerificationMode.None)
        {
            options.Append(" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null");
            return options.ToString();
        }

        var (host, port) = ParseSshHostAndPort(remoteUrl);
        var knownHostsContent = await ScanAndVerifyHostKeysAsync(host, port, trust, cancellationToken).ConfigureAwait(false);
        var knownHostsPath = Path.Combine(isolatedHome, "known_hosts");
        File.WriteAllText(knownHostsPath, knownHostsContent);

        options.Append(" -o StrictHostKeyChecking=yes -o UserKnownHostsFile=").Append(Quote(knownHostsPath));
        if (port != 22)
        {
            options.Append(" -p ").Append(port);
        }

        return options.ToString();
    }

    private static void ConfigureIsolatedEnvironment(ProcessStartInfo startInfo, string isolatedHome)
    {
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_ASKPASS", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("SSH_ASKPASS", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("SSH_ASKPASS_REQUIRE", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_SSH", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_SSH_COMMAND", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["HOME"] = isolatedHome;
        startInfo.Environment["XDG_CONFIG_HOME"] = isolatedHome;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    internal static void ConfigurePassphraseAskPass(ProcessStartInfo? startInfo, GitAuthentication authentication, string isolatedHome)
    {
        if (string.IsNullOrEmpty(authentication.SshPrivateKeyPassphrase))
        {
            return;
        }

        var askPassPath = Path.Combine(isolatedHome, "ssh-askpass.sh");
        File.WriteAllText(askPassPath, "#!/bin/sh\nprintf '%s\\n' \"$ISSUEAGENT_SSH_KEY_PASSPHRASE\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(askPassPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (startInfo is not null)
        {
            startInfo.Environment["SSH_ASKPASS"] = askPassPath;
            startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            startInfo.Environment["DISPLAY"] = "issueagent";
            startInfo.Environment["ISSUEAGENT_SSH_KEY_PASSPHRASE"] = authentication.SshPrivateKeyPassphrase;
        }
    }

    /// <summary>Parses the host and port from either <c>ssh://[user@]host[:port]/path</c> or SCP-like
    /// <c>[user@]host:path</c> remote URL forms.</summary>
    public static (string Host, int Port) ParseSshHostAndPort(string remoteUrl)
    {
        if (remoteUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            return (uri.Host, uri.Port == -1 ? 22 : uri.Port);
        }

        var match = ScpLikeUrlPattern().Match(remoteUrl);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not parse an SSH host from remote URL '{remoteUrl}'.");
        }

        return (match.Groups["host"].Value, 22);
    }

    /// <summary>Runs <c>ssh-keyscan</c> against <paramref name="host"/>, verifies each offered key's
    /// SHA-256 fingerprint against <paramref name="trust"/>, and returns only matching key lines
    /// suitable for a <c>known_hosts</c> file. Throws if none match.</summary>
    public static async ValueTask<string> ScanAndVerifyHostKeysAsync(string host, int port, SshTrust trust, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(new ProcessStartInfo("ssh-keyscan")
        {
            ArgumentList = { "-T", "5", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), host },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-keyscan failed for '{host}:{port}' with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        var matchedLines = new List<string>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            byte[] keyBlob;
            try
            {
                keyBlob = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException)
            {
                continue;
            }

            var fingerprint = "sha256:" + Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');
            if (trust.Fingerprints.Any(f => string.Equals(NormalizeFingerprint(f), NormalizeFingerprint(fingerprint), StringComparison.Ordinal)))
            {
                matchedLines.Add(line);
            }
        }

        if (matchedLines.Count == 0)
        {
            throw new InvalidOperationException($"No SSH host key offered by '{host}:{port}' matched a configured pinned fingerprint.");
        }

        return string.Join('\n', matchedLines) + "\n";
    }

    private static async ValueTask<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The child exited between checking its state and terminating its process tree.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return (process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private static string NormalizeFingerprint(string fingerprint)
    {
        var compact = WhitespacePattern().Replace(fingerprint, string.Empty).TrimStart(':');
        return compact.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? compact["sha256:".Length..].TrimStart(':')
            : compact;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void MakeKeyFilePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^(?:[^@]+@)?(?<host>[^:/]+):(?!//)")]
    private static partial Regex ScpLikeUrlPattern();
}
