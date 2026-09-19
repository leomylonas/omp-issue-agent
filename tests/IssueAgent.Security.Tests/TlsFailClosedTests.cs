using Xunit;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IssueAgent.Git;

namespace IssueAgent.Security.Tests;

/// <summary>Proves TLS trust is fail-closed against a real HTTPS server with a real (self-signed)
/// certificate — not a mocked handshake. Covers specification §9, §36 "TLS fail-closed test".</summary>
public sealed class TlsFailClosedTests
{
    [Fact]
    public async Task PinnedTrustRejectsServerWhoseCertificateFingerprintDoesNotMatch()
    {
        using var certificate = CreateSelfSignedCertificate();
        using var listener = await StartHttpsListenerAsync(certificate);

        var wrongFingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));
        var tlsTrust = new TlsTrust { Mode = TlsTrustMode.Pinned, Fingerprints = [wrongFingerprint] };
        using var client = new HttpClient(TlsHttpHandlerFactory.Create(tlsTrust));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync($"https://127.0.0.1:{listener.Port}/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PinnedTrustAcceptsServerWhoseCertificateFingerprintMatches()
    {
        using var certificate = CreateSelfSignedCertificate();
        using var listener = await StartHttpsListenerAsync(certificate);

        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        var tlsTrust = new TlsTrust { Mode = TlsTrustMode.Pinned, Fingerprints = [fingerprint] };
        using var client = new HttpClient(TlsHttpHandlerFactory.Create(tlsTrust));

        using var response = await client.GetAsync($"https://127.0.0.1:{listener.Port}/", TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task SystemTrustRejectsUntrustedSelfSignedServer()
    {
        using var certificate = CreateSelfSignedCertificate();
        using var listener = await StartHttpsListenerAsync(certificate);

        using var client = new HttpClient(TlsHttpHandlerFactory.Create(TlsTrust.System));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync($"https://127.0.0.1:{listener.Port}/", TestContext.Current.CancellationToken));
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=issueagent-security-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }

    private static async Task<TestHttpsListener> StartHttpsListenerAsync(X509Certificate2 certificate)
    {
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                using var tcpClient = await tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);
                await using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false, checkCertificateRevocation: false).ConfigureAwait(false);
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
                await sslStream.WriteAsync(response).ConfigureAwait(false);
            }
            catch
            {
                // The client is expected to abort the handshake for the rejection test cases.
            }
        });

        return new TestHttpsListener(tcpListener, port);
    }

    private sealed class TestHttpsListener(System.Net.Sockets.TcpListener listener, int port) : IDisposable
    {
        public int Port { get; } = port;

        public void Dispose() => listener.Stop();
    }
}
