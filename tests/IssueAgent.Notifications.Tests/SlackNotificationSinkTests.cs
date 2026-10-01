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
    public async Task SendAsyncThrowsOnNonSuccessStatus()
    {
        server
            .Given(Request.Create().WithPath("/services/T000/B000/XXXX").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var httpClient = new HttpClient();
        var sink = new SlackNotificationSink(httpClient, new Uri(server.Url! + "/services/T000/B000/XXXX"));
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanFailed, "github/octo/widgets", 7, "workflow-1", "Planning failed.");

        await Assert.ThrowsAsync<NotificationPostDispatchException>(() => sink.SendAsync(notification, CancellationToken.None));
    }

    public void Dispose()
    {
        server.Stop();
        server.Dispose();
    }
}
