using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IssueAgent.Git;

namespace IssueAgent.Git.Tests;

public sealed class AdditionalCaTrustStoreTests
{
    [Fact]
    public void AdditionalCaTrustRequiresServerAuthenticationEku()
    {
        using var authority = CreateAuthority();
        using var clientOnly = CreateLeaf(authority, "client.example", "1.3.6.1.5.5.7.3.2");
        using var server = CreateLeaf(authority, "server.example", "1.3.6.1.5.5.7.3.1");
        var caPath = Path.Combine(Path.GetTempPath(), $"issue-agent-ca-{Guid.NewGuid():N}.cer");
        File.WriteAllBytes(caPath, authority.Export(X509ContentType.Cert));

        try
        {
            Assert.False(AdditionalCaTrustStore.IsTrusted(clientOnly, [caPath], "client.example"));
            Assert.True(AdditionalCaTrustStore.IsTrusted(server, [caPath], "server.example"));
        }
        finally
        {
            File.Delete(caPath);
        }
    }

    private static X509Certificate2 CreateAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=IssueAgent test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 authority, string host, string ekuOid)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ekuOid)], true));
        return request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid().ToByteArray());
    }
}
