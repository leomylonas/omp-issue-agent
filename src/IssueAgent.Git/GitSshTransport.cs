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
    public static void CloneBare(string cloneUrl, string destinationPath, GitAuthentication authentication) =>
        RunGit(["clone", "--bare", "--", cloneUrl, destinationPath], workingDirectory: null, cloneUrl, authentication);

    public static void Fetch(string bareRepositoryPath, GitAuthentication authentication) =>
        RunGit(["fetch", "--all", "--prune"], bareRepositoryPath, GetOriginUrl(bareRepositoryPath), authentication);

    public static void Push(string worktreePath, string branchName, GitAuthentication authentication) =>
        RunGit(["push", "origin", "--", $"{branchName}:{branchName}"], worktreePath, GetOriginUrl(worktreePath), authentication);

    /// <summary>Initializes and fetches one submodule through the Git/SSH transport.</summary>
    public static void UpdateSubmodule(string repositoryPath, string submodulePath, string remoteUrl, GitAuthentication authentication) =>
        RunGit(["submodule", "update", "--init", "--", submodulePath], repositoryPath, remoteUrl, authentication);

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

    private static void RunGit(IReadOnlyList<string> arguments, string? workingDirectory, string remoteUrl, GitAuthentication authentication)
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
            var sshCommand = BuildSshCommand(authentication, trust, remoteUrl, isolatedHome);

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

            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            startInfo.Environment["HOME"] = isolatedHome;
            startInfo.Environment["XDG_CONFIG_HOME"] = isolatedHome;
            startInfo.Environment["GIT_SSH_COMMAND"] = sshCommand;
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr}");
            }
        }
        finally
        {
            Directory.Delete(isolatedHome, recursive: true);
        }
    }

    public static string BuildSshCommandForLfs(GitAuthentication authentication, SshTrust trust, string remoteUrl, string isolatedHome) =>
        BuildSshCommand(authentication, trust, remoteUrl, isolatedHome);

    private static string BuildSshCommand(GitAuthentication authentication, SshTrust trust, string remoteUrl, string isolatedHome)
    {
        var keyPath = Path.Combine(isolatedHome, "id_agent");
        File.WriteAllText(keyPath, authentication.SshPrivateKey ?? throw new InvalidOperationException("SSH authentication requires a private key."));
        MakeKeyFilePrivate(keyPath);

        if (!string.IsNullOrEmpty(authentication.SshPrivateKeyPassphrase))
        {
            throw new NotSupportedException("Passphrase-protected SSH keys require an ssh-agent; configure an unencrypted key or pre-load an agent.");
        }

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
        var knownHostsContent = ScanAndVerifyHostKeys(host, port, trust);
        var knownHostsPath = Path.Combine(isolatedHome, "known_hosts");
        File.WriteAllText(knownHostsPath, knownHostsContent);

        options.Append(" -o StrictHostKeyChecking=yes -o UserKnownHostsFile=").Append(Quote(knownHostsPath));
        if (port != 22)
        {
            options.Append(" -p ").Append(port);
        }

        return options.ToString();
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
    public static string ScanAndVerifyHostKeys(string host, int port, SshTrust trust)
    {
        using var process = Process.Start(new ProcessStartInfo("ssh-keyscan")
        {
            ArgumentList = { "-T", "5", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), host },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start ssh-keyscan.");

        var output = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-keyscan failed for '{host}:{port}' with exit code {process.ExitCode}: {stderr.Trim()}");
        }

        var matchedLines = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
