using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LibGit2Sharp;

namespace IssueAgent.Git;

/// <summary>Verifies a server certificate against explicitly configured additional CA files,
/// augmenting system trust in-process without touching the system/root certificate store.</summary>
public static class AdditionalCaTrustStore
{
    public static bool IsTrusted(Certificate certificate, IReadOnlyList<string> additionalCaCertificatePaths, string? host) =>
        certificate is CertificateX509 x509Certificate &&
        IsTrusted(x509Certificate.Certificate, additionalCaCertificatePaths, host);

    public static bool IsTrusted(X509Certificate certificate, IReadOnlyList<string> additionalCaCertificatePaths, string? host = null)
    {
        if (additionalCaCertificatePaths.Count == 0)
        {
            return false;
        }

        var leaf = new X509Certificate2(certificate);
        if (!string.IsNullOrEmpty(host) && !CertificateMatchesHost(leaf, host))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var caPath in additionalCaCertificatePaths)
        {
            chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificateFromFile(caPath));
        }

        return chain.Build(leaf);
    }

    private static readonly char[] SanSeparators = [',', '\n'];

    /// <summary>Matches the leaf certificate's Subject Alternative Names (falling back to the
    /// subject common name) against <paramref name="host"/>, so a CA trusted for one host cannot be
    /// used to impersonate an unrelated host.</summary>
    private static bool CertificateMatchesHost(X509Certificate2 leaf, string host)
    {
        var sanExtension = leaf.Extensions["2.5.29.17"];
        if (sanExtension is not null)
        {
            foreach (var line in sanExtension.Format(false).Split(SanSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var separatorIndex = line.IndexOf('=');
                if (separatorIndex < 0) continue;
                var value = line[(separatorIndex + 1)..].Trim();
                if (MatchesDnsPattern(value, host)) return true;
            }

            return false;
        }

        return MatchesDnsPattern(leaf.GetNameInfo(X509NameType.DnsName, false), host);
    }

    private static bool MatchesDnsPattern(string pattern, string host)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                   host.Length > suffix.Length &&
                   !host[..^suffix.Length].Contains('.', StringComparison.Ordinal);
        }

        return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Verifies a server certificate's SHA-256 fingerprint against one or more configured
/// pinned fingerprints.</summary>
public static class PinnedCertificateVerifier
{
    public static bool Matches(Certificate certificate, IReadOnlyList<string> fingerprints) =>
        certificate is CertificateX509 x509Certificate &&
        Matches(x509Certificate.Certificate, fingerprints);

    public static bool Matches(X509Certificate certificate, IReadOnlyList<string> fingerprints)
    {
        if (fingerprints.Count == 0)
        {
            return false;
        }

        var actual = ComputeSha256Fingerprint(certificate.GetRawCertData());
        return fingerprints.Any(configured => FingerprintsEqual(configured, actual));
    }

    private static string ComputeSha256Fingerprint(byte[] rawData) =>
        Convert.ToHexStringLower(SHA256.HashData(rawData));

    private static bool FingerprintsEqual(string configured, string actualHex)
    {
        var normalized = configured
            .Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim();
        return string.Equals(normalized, actualHex, StringComparison.OrdinalIgnoreCase);
    }
}
