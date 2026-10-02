using IssueAgent.Workflow;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace IssueAgent.Notifications.Tests;

public sealed class TelegramNotificationSinkTests : IDisposable
{
    private readonly WireMockServer server = WireMockServer.Start();

    [Fact]
    public async Task SendAsyncPostsFormattedMessageToBotEndpoint()
    {
        const string token = "123456789:AAHdqTcvAKrOexampleTokenText";
        server
            .Given(Request.Create().WithPath($"/bot{token}/sendMessage").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"ok":true}"""));

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.Url! + "/") };
        var sink = new TelegramNotificationSink(httpClient, token, "12345");
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanReady, "github/octo/widgets", 7, "workflow-1", "Plan is ready.");

        await sink.SendAsync(notification, CancellationToken.None);

        var request = Assert.Single(server.LogEntries);
        Assert.Contains("\"chat_id\":\"12345\"", request.RequestMessage!.Body, StringComparison.Ordinal);
        Assert.Contains("Plan is ready.", request.RequestMessage!.Body, StringComparison.Ordinal);
        Assert.Contains("PlanReady", request.RequestMessage!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsyncClassifiesRateLimitRejectionAsRetryable()
    {
        const string token = "123456789:AAHdqTcvAKrOexampleTokenText";
        server
            .Given(Request.Create().WithPath($"/bot{token}/sendMessage").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429));

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.Url! + "/") };
        var sink = new TelegramNotificationSink(httpClient, token, "12345");
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanReady, "github/octo/widgets", 7, "workflow-1", "Plan is ready.");

        var exception = await Assert.ThrowsAsync<NotificationDeliveryRejectedException>(() => sink.SendAsync(notification, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task SendAsyncClassifiesTransportFailureAsAmbiguousPost()
    {
        const string token = "123456789:AAHdqTcvAKrOexampleTokenText";
        using var httpClient = new HttpClient(new FailingPostHandler()) { BaseAddress = new Uri("https://api.telegram.org/") };
        var sink = new TelegramNotificationSink(httpClient, token, "12345");
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanReady, "github/octo/widgets", 7, "workflow-1", "Plan is ready.");

        await Assert.ThrowsAsync<NotificationPostDispatchException>(() => sink.SendAsync(notification, CancellationToken.None));
    }


    private sealed class FailingPostHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated connection reset."));
    }

    public void Dispose()
    {
        server.Stop();
        server.Dispose();
    }
}
