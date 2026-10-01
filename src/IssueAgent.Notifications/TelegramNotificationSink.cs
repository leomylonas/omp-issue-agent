using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IssueAgent.Workflow;

namespace IssueAgent.Notifications;

/// <summary>Telegram Bot API sink (specification §28). Posts to <c>sendMessage</c> on the
/// configured bot token's API base.</summary>
public sealed partial class TelegramNotificationSink(HttpClient httpClient, string botToken, string chatId) : INotificationSink
{
    public string Name => "telegram";

    public async Task SendAsync(WorkflowNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var payload = new TelegramSendMessageRequest(chatId, FormatMessage(notification));
        try
        {
            using var response = await httpClient
                .PostAsJsonAsync($"/bot{botToken}/sendMessage", payload, TelegramJsonContext.Default.TelegramSendMessageRequest, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new NotificationPostDispatchException("Telegram notification POST may have been delivered.", exception);
        }
    }

    private static string FormatMessage(WorkflowNotification notification) =>
        $"[IssueAgent] {notification.Kind} — {notification.RepositoryId} #{notification.IssueNumber}\n{notification.Message}";

    private sealed record TelegramSendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text);

    [JsonSerializable(typeof(TelegramSendMessageRequest))]
    private sealed partial class TelegramJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
