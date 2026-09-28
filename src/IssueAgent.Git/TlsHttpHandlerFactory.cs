using System.Net;
using System.Net.Security;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace IssueAgent.Git;

/// <summary>Creates HTTP handlers that apply the configured TLS trust policy for managed HTTP clients
/// (specification §9).</summary>
public static class TlsHttpHandlerFactory
{
    /// <param name="tlsTrust">The configured TLS trust policy to apply to every request.</param>
    /// <param name="allowAutoRedirect">When <see langword="false"/>, a 3xx response is returned to
    /// the caller instead of being followed — used for the anonymous attachment client, where
    /// following an unvalidated redirect could reach an internal/loopback/cloud-metadata endpoint
    /// the original URL check never saw (specification §15).</param>
    public static SocketsHttpHandler Create(TlsTrust tlsTrust, bool allowAutoRedirect = true)
    {
        ArgumentNullException.ThrowIfNull(tlsTrust);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
                    certificate is not null && IsTrusted(tlsTrust, certificate, policyErrors),
            },
        };
    }

    /// <summary>Request-scoped option carrying the exact address set an SSRF pre-check already
    /// validated as safe for one download. Read by <see cref="CreateForAnonymousAttachmentDownloads"/>'s
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> so the real connection is pinned to that set
    /// instead of letting the transport re-resolve DNS independently at connect time — closing the
    /// window a DNS-rebinding attacker would otherwise use to swap in an internal/loopback/cloud-
    /// metadata address between the safety check and the real connect (specification §15).</summary>
    public static readonly HttpRequestOptionsKey<IReadOnlySet<IPAddress>> ValidatedAddressesOptionKey = new("IssueAgent.ValidatedAddresses");

    /// <summary>Builds the handler used exclusively for downloading attachments from untrusted,
    /// caller-supplied URLs. Every request sent through the resulting <see cref="HttpClient"/> MUST
    /// set <see cref="ValidatedAddressesOptionKey"/> to the address set an SSRF pre-check already
    /// approved; the connect callback refuses the connection otherwise (fail closed — this handler
    /// has no other legitimate use) and refuses it again if DNS now resolves to an address outside
    /// that pre-approved set. A resolved address that is still a member of the originally-validated
    /// set is always accepted, so ordinary round-robin/failover across the advertised address set
    /// keeps working; only a genuinely new address — the DNS-rebinding signature — is rejected.</summary>
    public static SocketsHttpHandler CreateForAnonymousAttachmentDownloads(TlsTrust tlsTrust)
    {
        var handler = Create(tlsTrust, allowAutoRedirect: false);
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            if (!context.InitialRequestMessage.Options.TryGetValue(ValidatedAddressesOptionKey, out var validated) ||
                validated is not { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Refusing to connect to '{context.DnsEndPoint.Host}': no pre-validated address set was supplied for this request.");
            }

            var resolved = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            return await ConnectToValidatedAddressesAsync(
                context.DnsEndPoint.Host,
                context.DnsEndPoint.Port,
                resolved,
                validated,
                cancellationToken).ConfigureAwait(false);
        };
        return handler;
    }

    internal static async ValueTask<Stream> ConnectToValidatedAddressesAsync(
        string host,
        int port,
        IReadOnlyList<IPAddress> resolvedAddresses,
        IReadOnlySet<IPAddress> validatedAddresses,
        CancellationToken cancellationToken)
    {
        SocketException? lastConnectionFailure = null;
        var hasValidatedAddress = false;

        foreach (var address in resolvedAddresses)
        {
            if (!validatedAddresses.Contains(address))
            {
                continue;
            }

            hasValidatedAddress = true;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastConnectionFailure = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        if (!hasValidatedAddress)
        {
            throw new InvalidOperationException(
                $"Refusing to connect to '{host}': its DNS answer no longer matches any address validated moments ago (possible DNS-rebinding attempt).");
        }

        throw lastConnectionFailure!;
    }


    private static bool IsTrusted(TlsTrust tlsTrust, X509Certificate certificate, SslPolicyErrors policyErrors)
    {
        return tlsTrust.Mode switch
        {
            TlsTrustMode.System => policyErrors == SslPolicyErrors.None,
            TlsTrustMode.None => true,
            TlsTrustMode.SystemPlusAdditionalCa => policyErrors == SslPolicyErrors.None ||
                (policyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                AdditionalCaTrustStore.IsTrusted(certificate, tlsTrust.AdditionalCaCertificatePaths),
            TlsTrustMode.Pinned => PinnedCertificateVerifier.Matches(certificate, tlsTrust.Fingerprints),
            _ => false,
        };
    }
}
