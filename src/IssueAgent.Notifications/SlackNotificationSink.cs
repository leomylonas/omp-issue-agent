using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IssueAgent.Workflow;

namespace IssueAgent.Notifications;

/// <summary>Slack incoming-webhook sink (specification §28).</summary>
public sealed partial class SlackNotificationSink(HttpClient httpClient, Uri webhookUrl) : INotificationSink
{
    public string Name => "slack";

    public async Task SendAsync(WorkflowNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var payload = new SlackIncomingWebhookRequest(FormatMessage(notification));
        using var response = await httpClient
            .PostAsJsonAsync(webhookUrl, payload, SlackJsonContext.Default.SlackIncomingWebhookRequest, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string FormatMessage(WorkflowNotification notification) =>
        $"*[IssueAgent]* {notification.Kind} — {notification.RepositoryId} #{notification.IssueNumber}\n{notification.Message}";

    private sealed record SlackIncomingWebhookRequest([property: JsonPropertyName("text")] string Text);

    [JsonSerializable(typeof(SlackIncomingWebhookRequest))]
    private sealed partial class SlackJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
