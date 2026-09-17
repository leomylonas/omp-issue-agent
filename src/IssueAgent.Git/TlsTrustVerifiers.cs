using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LibGit2Sharp;

namespace IssueAgent.Git;

/// <summary>Verifies a server certificate against explicitly configured additional CA files,
/// augmenting system trust in-process without touching the system/root certificate store.</summary>
public static class AdditionalCaTrustStore
{
    public static bool IsTrusted(Certificate certificate, IReadOnlyList<string> additionalCaCertificatePaths)
    {
        if (certificate is not CertificateX509 x509Certificate || additionalCaCertificatePaths.Count == 0)
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

        return chain.Build(new X509Certificate2(x509Certificate.Certificate));
    }
}

/// <summary>Verifies a server certificate's SHA-256 fingerprint against one or more configured
/// pinned fingerprints.</summary>
public static class PinnedCertificateVerifier
{
    public static bool Matches(Certificate certificate, IReadOnlyList<string> fingerprints)
    {
        if (certificate is not CertificateX509 x509Certificate || fingerprints.Count == 0)
        {
            return false;
        }

        var actual = ComputeSha256Fingerprint(x509Certificate.Certificate.GetRawCertData());
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
