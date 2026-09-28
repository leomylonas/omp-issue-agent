using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
namespace IssueAgent.Git.Tests;

public sealed class TlsHttpHandlerFactoryTests : IAsyncLifetime
{
    private static readonly byte[] OkResponse = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource listenerCancellation = new();
    private readonly Task server;
    private readonly Uri baseUri;

    public TlsHttpHandlerFactoryTests()
    {
        listener.Start();
        baseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        server = Task.Run(() => ServeAsync(listenerCancellation.Token));
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();
                await stream.WriteAsync(OkResponse, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [Fact]
    public async Task ConnectCallbackSucceedsWhenTheResolvedAddressIsInTheValidatedSet()
    {
        using var httpClient = new HttpClient(TlsHttpHandlerFactory.CreateForAnonymousAttachmentDownloads(TlsTrust.System));
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
        request.Options.Set(TlsHttpHandlerFactory.ValidatedAddressesOptionKey, new HashSet<IPAddress> { IPAddress.Parse("127.0.0.1") });

        using var response = await httpClient.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConnectCallbackFallsBackAcrossValidatedAddressesWithoutConnectingToUnvalidatedDnsAnswers()
    {
        await using var stream = await TlsHttpHandlerFactory.ConnectToValidatedAddressesAsync(
            "attachment.example",
            baseUri.Port,
            [
                IPAddress.Parse("127.0.0.3"),
                IPAddress.Parse("127.0.0.2"),
                IPAddress.Loopback,
            ],
            new HashSet<IPAddress>
            {
                IPAddress.Parse("127.0.0.2"),
                IPAddress.Loopback,
            },
            TestContext.Current.CancellationToken);

        await stream.WriteAsync(
            "GET / HTTP/1.1\r\nHost: attachment.example\r\nConnection: close\r\n\r\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        var buffer = new byte[1024];
        var bytesRead = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Contains("200 OK", Encoding.ASCII.GetString(buffer, 0, bytesRead), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectCallbackFailsClosedWhenNoValidatedAddressSetWasSupplied()
    {
        // Regression: the anonymous attachment client has no legitimate use without a prior SSRF
        // safety check; a request that somehow reaches it without one must be refused, not silently
        // connected (specification §15).
        using var httpClient = new HttpClient(TlsHttpHandlerFactory.CreateForAnonymousAttachmentDownloads(TlsTrust.System));
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);

        var exception = await Record.ExceptionAsync(() => httpClient.SendAsync(request, CancellationToken.None));

        Assert.NotNull(exception);
        Assert.Contains("no pre-validated address set", exception!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectCallbackRejectsAConnectionWhenDnsNowResolvesOutsideTheValidatedSet()
    {
        // Regression (DNS-rebinding TOCTOU): the address DNS resolves to at connect time must be a
        // member of the set an earlier SSRF check already validated. A validated set that no longer
        // matches (simulating an attacker's second, rebound lookup returning something new) must be
        // refused rather than silently connected to whatever the fresh resolution now returns.
        using var httpClient = new HttpClient(TlsHttpHandlerFactory.CreateForAnonymousAttachmentDownloads(TlsTrust.System));
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
        request.Options.Set(TlsHttpHandlerFactory.ValidatedAddressesOptionKey, new HashSet<IPAddress> { IPAddress.Parse("203.0.113.1") });

        var exception = await Record.ExceptionAsync(() => httpClient.SendAsync(request, CancellationToken.None));

        Assert.NotNull(exception);
        Assert.Contains("no longer matches", exception!.ToString(), StringComparison.Ordinal);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        listenerCancellation.Cancel();
        listener.Stop();
        await server.ConfigureAwait(false);
        listenerCancellation.Dispose();
    }
}
