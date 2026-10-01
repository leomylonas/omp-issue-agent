using IssueAgent.Workflow;
using IssueAgent.Domain;


namespace IssueAgent.Notifications;

/// <summary>One independent, named notification destination. Sinks never share failure state:
/// a failure in one sink never prevents another sink from being tried, and never blocks the
/// workflow transition that triggered the notification.</summary>
public interface INotificationSink
{
    string Name { get; }

    /// <summary>Whether the destination accepts an idempotency key for notification sends.</summary>
    bool SupportsIdempotency => false;

    Task SendAsync(WorkflowNotification notification, CancellationToken cancellationToken);
}

/// <summary>Indicates that a notification POST may have reached its destination, so replaying it
/// could produce a duplicate.</summary>
public sealed class NotificationPostDispatchException(string message, Exception innerException) : HttpRequestException(message, innerException);

public delegate void NotificationSinkFailureHandler(string sinkName, WorkflowNotification notification, Exception exception);

/// <summary>
/// Generic fan-out notification abstraction (specification §28). Workflow notifications are
/// distinct from operational alerts. Per-event routing can select one or more sinks by name; when
/// routing does not name any sink for an event kind, the notification goes to every enabled sink.
/// </summary>
public sealed class FanOutNotifier(
    IReadOnlyList<INotificationSink> sinks,
    RetryPolicy retryPolicy,
    IReadOnlyDictionary<WorkflowNotificationKind, IReadOnlySet<string>>? routing = null,
    NotificationSinkFailureHandler? onSinkFailure = null) : IWorkflowNotifier
{

    public async Task NotifyAsync(WorkflowNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var targets = SelectTargets(notification.Kind);
        var sendTasks = targets.Select(sink => SendToSinkAsync(sink, notification, cancellationToken));
        await Task.WhenAll(sendTasks).ConfigureAwait(false);
    }

    private IEnumerable<INotificationSink> SelectTargets(WorkflowNotificationKind kind)
    {
        if (routing is not null && routing.TryGetValue(kind, out var names) && names.Count > 0)
        {
            return sinks.Where(sink => names.Contains(sink.Name));
        }

        return sinks;
    }

    private async Task SendToSinkAsync(INotificationSink sink, WorkflowNotification notification, CancellationToken cancellationToken)
    {
        var failure = await retryPolicy
            .ExecuteAsync(
                ct => sink.SendAsync(notification, ct),
                cancellationToken,
                exception => sink.SupportsIdempotency || exception is not NotificationPostDispatchException)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            onSinkFailure?.Invoke(sink.Name, notification, failure);
        }
    }
}
