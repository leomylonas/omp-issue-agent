using IssueAgent.Workflow;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace IssueAgent.Notifications.Tests;

public sealed class SlackNotificationSinkTests : IDisposable
{
    private readonly WireMockServer server = WireMockServer.Start();

    [Fact]
    public async Task SendAsyncPostsFormattedMessageToWebhook()
    {
        server
            .Given(Request.Create().WithPath("/services/T000/B000/XXXX").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        using var httpClient = new HttpClient();
        var sink = new SlackNotificationSink(httpClient, new Uri(server.Url! + "/services/T000/B000/XXXX"));
        var notification = new WorkflowNotification(WorkflowNotificationKind.ImplementationReady, "github/octo/widgets", 7, "workflow-1", "Draft PR is ready.");

        await sink.SendAsync(notification, CancellationToken.None);

        var request = Assert.Single(server.LogEntries);
        Assert.Contains("Draft PR is ready.", request.RequestMessage!.Body, StringComparison.Ordinal);
        Assert.Contains("ImplementationReady", request.RequestMessage!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsyncClassifiesServerRejectionAsRetryable()
    {
        server
            .Given(Request.Create().WithPath("/services/T000/B000/XXXX").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var httpClient = new HttpClient();
        var sink = new SlackNotificationSink(httpClient, new Uri(server.Url! + "/services/T000/B000/XXXX"));
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanFailed, "github/octo/widgets", 7, "workflow-1", "Planning failed.");

        var exception = await Assert.ThrowsAsync<NotificationDeliveryRejectedException>(() => sink.SendAsync(notification, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task SendAsyncClassifiesTransportFailureAsAmbiguousPost()
    {
        using var httpClient = new HttpClient(new FailingPostHandler());
        var sink = new SlackNotificationSink(httpClient, new Uri("https://hooks.slack.com/services/T000/B000/XXXX"));
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanFailed, "github/octo/widgets", 7, "workflow-1", "Planning failed.");

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
