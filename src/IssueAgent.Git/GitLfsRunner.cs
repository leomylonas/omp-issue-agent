using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
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
        string canonicalRemoteUrl,
        GitAuthentication authentication,
        CancellationToken cancellationToken)
    {
        await EnsureFiltersRegisteredAsync(worktreePath, canonicalRemoteUrl, authentication, cancellationToken).ConfigureAwait(false);
        await RunAsync(worktreePath, canonicalRemoteUrl, authentication, cancellationToken, "lfs", "pull").ConfigureAwait(false);
    }

    /// <summary>Uploads every LFS object referenced by <paramref name="branchName"/> that the remote
    /// does not already have. Must complete successfully before the corresponding <c>git push</c>
    /// publishes the ref; a failure here means the publication failed even if a later plain push
    /// would have succeeded.</summary>
    public static async Task UploadObjectsAsync(
        string worktreePath,
        string canonicalRemoteUrl,
        string branchName,
        GitAuthentication authentication,
        CancellationToken cancellationToken)
    {
        await EnsureFiltersRegisteredAsync(worktreePath, canonicalRemoteUrl, authentication, cancellationToken).ConfigureAwait(false);
        await RunAsync(worktreePath, canonicalRemoteUrl, authentication, cancellationToken, "lfs", "push", "origin", "--", branchName).ConfigureAwait(false);
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
        string canonicalRemoteUrl,
        GitAuthentication authentication,
        CancellationToken cancellationToken) =>
        RunAsync(worktreePath, canonicalRemoteUrl, authentication, cancellationToken, "lfs", "install", "--local", "--skip-repo");

    private static async Task RunAsync(
        string worktreePath,
        string canonicalRemoteUrl,
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

            ApplyAuthentication(startInfo, canonicalRemoteUrl, authentication, isolatedHome);

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

    private static void ApplyAuthentication(
        ProcessStartInfo startInfo,
        string canonicalRemoteUrl,
        GitAuthentication authentication,
        string isolatedHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRemoteUrl);
        switch (authentication.Mode)
        {
            case GitAuthenticationMode.ProviderToken or GitAuthenticationMode.Token:
                var canonicalRemote = TryGetHttpsUri(canonicalRemoteUrl);
                var worktreeOrigin = TryGetHttpsUri(GetWorktreeOriginUrl(startInfo.WorkingDirectory));
                var endpoints = GetConfiguredLfsEndpoints(startInfo.WorkingDirectory);
                if (endpoints.Any(endpoint => TryGetHttpsUri(endpoint) is null))
                {
                    throw new InvalidOperationException(
                        "Every configured Git LFS pull or push endpoint must be an absolute HTTPS URI before credentials can be used.");
                }
                if (canonicalRemote is null ||
                    worktreeOrigin is null ||
                    !string.Equals(worktreeOrigin.Authority, canonicalRemote.Authority, StringComparison.OrdinalIgnoreCase) ||
                    endpoints.Any(endpoint => !IsHttpsEndpointAtAuthority(endpoint, canonicalRemote.Authority)))
                {
                    // A worktree or committed LFS override can redirect either pulls or pushes.
                    // Never offer the repository token unless every configured endpoint is HTTPS at
                    // the exact protected canonical remote authority.
                    break;
                }

                ApplyHttpsTlsTrust(startInfo, authentication.TlsTrust, isolatedHome);

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
                // Hybrid LFS authentication obtains credentials over SSH and then transfers the
                // objects over HTTPS. Apply the same HTTPS CA policy before git-lfs starts.
                ApplyHttpsTlsTrust(startInfo, authentication.TlsTrust, isolatedHome);
                var remoteUrl = canonicalRemoteUrl;
                startInfo.Environment["GIT_SSH_COMMAND"] = GitSshTransport.BuildSshCommandForLfs(authentication, trust, remoteUrl, isolatedHome);
                break;

            case GitAuthenticationMode.Anonymous:
                break;
        }
    }

    private static string GetWorktreeOriginUrl(string workingDirectory)
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


    /// <summary>Gets every endpoint override that can redirect an LFS pull or push. Both the
    /// worktree config and a committed <c>.lfsconfig</c> participate in git-lfs resolution.</summary>
    private static List<string> GetConfiguredLfsEndpoints(string workingDirectory)
    {
        string[] keys =
        [
            "lfs.url",
            "lfs.pushurl",
            "remote.origin.lfsurl",
            "remote.origin.lfspushurl",
            "remote.origin.pushurl",
        ];

        var endpoints = new List<string>();
        foreach (var key in keys)
        {
            endpoints.AddRange(GetConfigValues(workingDirectory, key, filePath: null));

            var lfsConfigPath = Path.Combine(workingDirectory, ".lfsconfig");
            if (File.Exists(lfsConfigPath))
            {
                endpoints.AddRange(GetConfigValues(workingDirectory, key, lfsConfigPath));
            }
        }
        return endpoints;
    }

    private static string[] GetConfigValues(string workingDirectory, string key, string? filePath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.hooksPath=/dev/null");
        if (filePath is null)
        {
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(workingDirectory);
            startInfo.ArgumentList.Add("config");
            startInfo.ArgumentList.Add("--local");
        }
        else
        {
            startInfo.ArgumentList.Add("config");
            startInfo.ArgumentList.Add("--file");
            startInfo.ArgumentList.Add(filePath);
        }
        startInfo.ArgumentList.Add("--get-all");
        startInfo.ArgumentList.Add(key);
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        var values = process.StandardOutput.ReadToEnd()
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        process.WaitForExit();
        return values;
    }

    private static bool IsHttpsEndpointAtAuthority(string endpoint, string authority) =>
        TryGetHttpsUri(endpoint) is { } uri &&
        string.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase);

    private static Uri? TryGetHttpsUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(uri.Host)
            ? uri
            : null;

    private static void ApplyHttpsTlsTrust(ProcessStartInfo startInfo, TlsTrust tlsTrust, string isolatedHome)
    {
        switch (tlsTrust.Mode)
        {
            case TlsTrustMode.System:
                return;
            case TlsTrustMode.SystemPlusAdditionalCa:
                startInfo.Environment["GIT_SSL_CAINFO"] = CreateCaBundle(tlsTrust.AdditionalCaCertificatePaths, isolatedHome);
                return;
            case TlsTrustMode.None:
                startInfo.Environment["GIT_SSL_NO_VERIFY"] = "true";
                return;
            case TlsTrustMode.Pinned:
                throw new InvalidOperationException(
                    "git-lfs cannot enforce certificate fingerprints. Configure system trust or system-plus-additional-ca trust for Git LFS HTTPS transfers.");
            default:
                throw new InvalidOperationException($"git-lfs cannot safely enforce the configured TLS trust mode '{tlsTrust.Mode}'.");
        }
    }

    private static string CreateCaBundle(IReadOnlyList<string> certificatePaths, string isolatedHome)
    {
        if (certificatePaths.Count == 0)
        {
            throw new InvalidOperationException("git-lfs system-plus-additional-ca trust requires at least one CA certificate path.");
        }

        var systemBundlePath = GetSystemCaBundlePath();
        var bundlePath = Path.Combine(isolatedHome, "system-plus-additional-ca-bundle.pem");
        using var destination = File.Create(bundlePath);
        CopyCertificateFile(systemBundlePath, destination);
        foreach (var certificatePath in certificatePaths)
        {
            CopyAdditionalCertificateAsPem(certificatePath, destination);
        }

        return bundlePath;
    }

    private static void CopyCertificateFile(string certificatePath, Stream destination)
    {
        using var source = File.OpenRead(certificatePath);
        source.CopyTo(destination);
        destination.WriteByte((byte)'\n');
    }

    private static void CopyAdditionalCertificateAsPem(string certificatePath, Stream destination)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        destination.Write(System.Text.Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()));
        destination.WriteByte((byte)'\n');
    }


    private static string GetSystemCaBundlePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SSL_CERT_FILE"),
            Environment.GetEnvironmentVariable("GIT_SSL_CAINFO"),
            "/etc/ssl/certs/ca-certificates.crt",
            "/etc/pki/tls/certs/ca-bundle.crt",
            "/etc/ssl/ca-bundle.pem",
        };
        var bundlePath = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        return bundlePath ?? throw new InvalidOperationException(
            "git-lfs cannot locate the system CA bundle required for system-plus-additional-ca trust.");
    }

}

public sealed class GitLfsUnavailableException(string message) : Exception(message);
