using System.Net;
using System.Net.Http;

namespace IssueAgent.Git.Tests;

public sealed class TlsHttpHandlerFactoryTests : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Uri baseUri;

    public TlsHttpHandlerFactoryTests()
    {
        var port = GetFreeTcpPort();
        baseUri = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(baseUri.ToString());
        listener.Start();
        _ = Task.Run(ServeOnceAsync);
    }

    private async Task ServeOnceAsync()
    {
        try
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                context.Response.StatusCode = 200;
                var body = "ok"u8.ToArray();
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                context.Response.OutputStream.Close();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException)
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

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        listener.Stop();
        listener.Close();
    }
}
