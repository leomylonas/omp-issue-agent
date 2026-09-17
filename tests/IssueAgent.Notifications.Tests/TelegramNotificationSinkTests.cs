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
        server
            .Given(Request.Create().WithPath("/bottest-token/sendMessage").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"ok":true}"""));

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.Url! + "/") };
        var sink = new TelegramNotificationSink(httpClient, "test-token", "12345");
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanReady, "github/octo/widgets", 7, "workflow-1", "Plan is ready.");

        await sink.SendAsync(notification, CancellationToken.None);

        var request = Assert.Single(server.LogEntries);
        Assert.Contains("\"chat_id\":\"12345\"", request.RequestMessage!.Body, StringComparison.Ordinal);
        Assert.Contains("Plan is ready.", request.RequestMessage!.Body, StringComparison.Ordinal);
        Assert.Contains("PlanReady", request.RequestMessage!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsyncThrowsOnNonSuccessStatus()
    {
        server
            .Given(Request.Create().WithPath("/bottest-token/sendMessage").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.Url! + "/") };
        var sink = new TelegramNotificationSink(httpClient, "test-token", "12345");
        var notification = new WorkflowNotification(WorkflowNotificationKind.PlanReady, "github/octo/widgets", 7, "workflow-1", "Plan is ready.");

        await Assert.ThrowsAsync<HttpRequestException>(() => sink.SendAsync(notification, CancellationToken.None));
    }

    public void Dispose()
    {
        server.Stop();
        server.Dispose();
    }
}
