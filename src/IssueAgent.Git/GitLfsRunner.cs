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
    public static Task MaterializeContentAsync(
        string worktreePath,
        string canonicalRemoteUrl,
        GitAuthentication authentication,
        CancellationToken cancellationToken) =>
        RunAsync(worktreePath, canonicalRemoteUrl, authentication, cancellationToken, "lfs", "pull");

    /// <summary>Uploads every LFS object referenced by <paramref name="branchName"/> that the remote
    /// does not already have. Must complete successfully before the corresponding <c>git push</c>
    /// publishes the ref; a failure here means the publication failed even if a later plain push
    /// would have succeeded.</summary>
    public static Task UploadObjectsAsync(
        string worktreePath,
        string canonicalRemoteUrl,
        string branchName,
        GitAuthentication authentication,
        CancellationToken cancellationToken) =>
        RunAsync(worktreePath, canonicalRemoteUrl, authentication, cancellationToken, "lfs", "push", "origin", "--", branchName);

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
            // Worktree-local config may be stored in the bare repository's common config. Supply
            // the complete filter registration on the isolated invocation so git-lfs never needs
            // `git lfs install` to discover it.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("filter.lfs.clean=git-lfs clean -- %f");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("filter.lfs.smudge=git-lfs smudge -- %f");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("filter.lfs.process=git-lfs filter-process");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("filter.lfs.required=true");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("lfs.repositoryformatversion=0");
            AddTrustedLfsEndpointConfiguration(startInfo, canonicalRemoteUrl);

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ConfigureIsolatedEnvironment(startInfo, isolatedHome);

            await ApplyAuthenticationAsync(startInfo, canonicalRemoteUrl, authentication, isolatedHome, cancellationToken).ConfigureAwait(false);

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

    private static void ConfigureIsolatedEnvironment(ProcessStartInfo startInfo, string isolatedHome)
    {
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_ASKPASS", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("SSH_ASKPASS", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_SSH", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_SSH_COMMAND", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_DIR", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_WORK_TREE", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_COMMON_DIR", StringComparison.OrdinalIgnoreCase) ||
                                   key.Equals("GIT_INDEX_FILE", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["HOME"] = isolatedHome;
        startInfo.Environment["XDG_CONFIG_HOME"] = isolatedHome;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "0";
    }

    private static async Task ApplyAuthenticationAsync(
        ProcessStartInfo startInfo,
        string canonicalRemoteUrl,
        GitAuthentication authentication,
        string isolatedHome,
        CancellationToken cancellationToken)
    {
        // LFS transfers to an HTTPS canonical remote can be anonymous. TLS policy is a transport
        // boundary, not an authentication concern, so apply it before selecting credentials.
        var canonicalHttpsRemote = TryGetHttpsUri(canonicalRemoteUrl);
        if (canonicalHttpsRemote is not null)
        {
            await ApplyHttpsTlsTrustAsync(startInfo, authentication.TlsTrust, isolatedHome, canonicalHttpsRemote, cancellationToken).ConfigureAwait(false);
        }

        switch (authentication.Mode)
        {
            case GitAuthenticationMode.ProviderToken or GitAuthenticationMode.Token:
                if (canonicalHttpsRemote is null)
                {
                    // The authenticated LFS endpoint is explicitly derived from the configured,
                    // canonical clone URL below. Never consult mutable worktree config for it.
                    break;
                }

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
                // objects over HTTPS. An SSH canonical remote did not take the HTTPS path above,
                // so apply the same HTTPS CA policy before git-lfs starts.
                if (canonicalHttpsRemote is null)
                {
                    if (!TryGetHttpsLfsEndpoint(canonicalRemoteUrl, out var lfsEndpoint))
                    {
                        throw new InvalidOperationException("Could not resolve the HTTPS LFS endpoint for SSH authentication.");
                    }

                    await ApplyHttpsTlsTrustAsync(startInfo, authentication.TlsTrust, isolatedHome, lfsEndpoint, cancellationToken).ConfigureAwait(false);
                }

                var remoteUrl = canonicalRemoteUrl;
                startInfo.Environment["GIT_SSH_COMMAND"] = await GitSshTransport.BuildSshCommandForLfsAsync(authentication, trust, remoteUrl, isolatedHome, cancellationToken).ConfigureAwait(false);
                GitSshTransport.ConfigurePassphraseAskPass(startInfo, authentication, isolatedHome);
                break;

            case GitAuthenticationMode.Anonymous:
                break;
        }
    }

    private static void AddTrustedLfsEndpointConfiguration(
        ProcessStartInfo startInfo,
        string canonicalRemoteUrl)
    {
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");

        if (!TryGetLfsRemoteUri(canonicalRemoteUrl, out var remote))
        {
            return;
        }

        // git-lfs reads repository-local lfs.url/lfs.pushurl, which OMP can modify while working.
        // Command-line config has precedence, so force both operations to the configured remote
        // rather than accepting a same-host path redirect from mutable worktree config. SSH uses
        // the canonical repository URL for its authentication handshake; HTTPS uses its LFS API.
        var repositoryUrl = remote.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var endpoint = remote.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase)
            ? repositoryUrl
            : repositoryUrl + "/info/lfs";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"lfs.url={endpoint}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"lfs.pushurl={endpoint}");
    }

    private static bool TryGetLfsRemoteUri(string url, out Uri remote)
    {
        if ((Uri.TryCreate(url, UriKind.Absolute, out remote!) ||
             TryNormalizeScpLikeSshRemote(url, out remote)) &&
            (remote.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             remote.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(remote.Host))
        {
            return true;
        }

        remote = null!;
        return false;
    }

    /// <summary>
    /// Resolves the HTTPS LFS API endpoint used after an SSH LFS authentication handshake. Pinning
    /// the SSH authority itself would probe port 22 as TLS and leave the real object transfer
    /// endpoint unpinned.
    /// </summary>
    private static bool TryGetHttpsLfsEndpoint(string url, out Uri endpoint)
    {
        if (!TryGetLfsRemoteUri(url, out var remote))
        {
            endpoint = null!;
            return false;
        }

        if (remote.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            endpoint = new Uri(remote.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/info/lfs");
            return true;
        }

        if (!remote.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = null!;
            return false;
        }

        var httpsRepository = new UriBuilder(remote)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
            UserName = string.Empty,
            Password = string.Empty,
        }.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        endpoint = new Uri(httpsRepository + "/info/lfs");
        return true;
    }

    private static bool TryNormalizeScpLikeSshRemote(string url, out Uri remote)
    {
        var colon = url.IndexOf(':');
        if (colon <= 0 || colon == url.Length - 1 || url.AsSpan(colon).StartsWith("://"))
        {
            remote = null!;
            return false;
        }

        var authority = url[..colon];
        if (authority.Contains('/') || authority.Contains('\\') || string.IsNullOrWhiteSpace(authority))
        {
            remote = null!;
            return false;
        }

        return Uri.TryCreate($"ssh://{authority}/{url[(colon + 1)..]}", UriKind.Absolute, out remote!);
    }

    private static Uri? TryGetHttpsUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(uri.Host)
            ? uri
            : null;


    private static async Task ApplyHttpsTlsTrustAsync(
        ProcessStartInfo startInfo,
        TlsTrust tlsTrust,
        string isolatedHome,
        Uri remote,
        CancellationToken cancellationToken)
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
                startInfo.Environment["GIT_SSL_CAINFO"] = await CreatePinnedCertificateBundleAsync(remote, tlsTrust.Fingerprints, isolatedHome, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"git-lfs cannot safely enforce the configured TLS trust mode '{tlsTrust.Mode}'.");
        }
    }

    /// <summary>Creates a git-lfs CA bundle from the endpoint certificate after checking its configured pin.</summary>
    private static async Task<string> CreatePinnedCertificateBundleAsync(
        Uri remote,
        IReadOnlyList<string> fingerprints,
        string isolatedHome,
        CancellationToken cancellationToken)
    {
        if (fingerprints.Count == 0)
        {
            throw new InvalidOperationException("git-lfs pinned TLS trust requires at least one certificate fingerprint.");
        }

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(remote.Host, remote.Port is > 0 ? remote.Port : 443, cancellationToken).ConfigureAwait(false);
        using var tls = new System.Net.Security.SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        X509Certificate2? certificate = null;
        await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = remote.Host,
            RemoteCertificateValidationCallback = (_, presented, _, _) =>
            {
                certificate = presented is null ? null : new X509Certificate2(presented);
                return certificate is not null && PinnedCertificateVerifier.Matches(certificate, fingerprints);
            },
        }, cancellationToken).ConfigureAwait(false);

        using (certificate ?? throw new InvalidOperationException($"LFS TLS endpoint '{remote.Host}' did not present a certificate."))
        {
            if (!PinnedCertificateVerifier.Matches(certificate, fingerprints))
            {
                throw new InvalidOperationException($"LFS TLS certificate for '{remote.Host}' did not match a configured pinned fingerprint.");
            }

            var bundlePath = Path.Combine(isolatedHome, "pinned-lfs-certificate.pem");
            File.WriteAllText(bundlePath, certificate.ExportCertificatePem());
            return bundlePath;
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
