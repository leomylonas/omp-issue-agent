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

    /// <summary>True when any committed <c>.gitattributes</c> in the worktree declares an LFS
    /// filter, meaning LFS materialization/upload is required rather than optional.</summary>
    public static bool RepositoryRequiresLfs(string worktreePath) =>
        Directory.EnumerateFiles(worktreePath, ".gitattributes", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }).Any(attributesPath =>
            File.ReadLines(attributesPath).Any(line =>
                line.Contains("filter=lfs", StringComparison.Ordinal)));

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


    /// <summary>Registers git-lfs's repository marker without installing hooks.</summary>
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
            // git-lfs uses this marker to recognize the explicitly provisioned filters. Supply
            // it per process rather than writing `git lfs install` state into the worktree.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("lfs.repositoryformatversion=0");
            AddTrustedLfsEndpointConfiguration(startInfo, canonicalRemoteUrl, authentication);

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
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
                if (canonicalRemote is null)
                {
                    // The authenticated LFS endpoint is explicitly derived from the configured,
                    // canonical clone URL below. Never consult mutable worktree config for it.
                    break;
                }

                ApplyHttpsTlsTrust(startInfo, authentication.TlsTrust, isolatedHome);

                var askPassPath = Path.Combine(isolatedHome, "askpass.sh");
                File.WriteAllText(
                    askPassPath,
                    "#!/bin/sh\ncase \"$1\" in\n  *Username*|*username*) printf '%s\\n' \"$ISSUEAGENT_GIT_USERNAME\" ;;\n  *) printf '%s\\n' \"$ISSUEAGENT_GIT_TOKEN\" ;;\nesac\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(askPassPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                startInfo.Environment["GIT_ASKPASS"] = askPassPath;
                startInfo.Environment["ISSUEAGENT_GIT_TOKEN"] = authentication.HttpsToken!;
                startInfo.Environment["ISSUEAGENT_GIT_USERNAME"] = authentication.HttpsUsername ?? "x-access-token";
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

    private static void AddTrustedLfsEndpointConfiguration(
        ProcessStartInfo startInfo,
        string canonicalRemoteUrl,
        GitAuthentication authentication)
    {
        if (authentication.Mode is not (GitAuthenticationMode.ProviderToken or GitAuthenticationMode.Token) ||
            TryGetHttpsUri(canonicalRemoteUrl) is not { } remote)
        {
            return;
        }

        // git-lfs reads repository-local lfs.url/lfs.pushurl, which OMP can modify while working.
        // Command-line config has precedence, so force both operations to the configured remote
        // rather than accepting a same-host path redirect from mutable worktree config.
        var endpoint = new Uri(remote.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/info/lfs").AbsoluteUri;
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"lfs.url={endpoint}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"lfs.pushurl={endpoint}");
    }

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
        var bytes = File.ReadAllBytes(certificatePath);
        if (System.Text.Encoding.ASCII.GetString(bytes).Contains(
                "-----BEGIN CERTIFICATE-----",
                StringComparison.Ordinal))
        {
            // A PEM input can intentionally contain the issuing intermediate(s). Preserve that
            // chain verbatim rather than loading/exporting only the first certificate.
            destination.Write(bytes);
            destination.WriteByte((byte)'\n');
            return;
        }

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
