using IssueAgent.Workflow;
using Microsoft.Extensions.Logging;

namespace IssueAgent.Observability;

/// <summary>Wraps an <see cref="IWorkflowNotifier"/> to record notification failures as a metric
/// (specification §30 "notification failures") and log the outcome. Workflow notifications are
/// distinct from operational alerts; this only observes, it never changes delivery behavior.</summary>
public sealed class ObservableWorkflowNotifier(IWorkflowNotifier inner, IssueAgentMetrics metrics, ILogger<ObservableWorkflowNotifier> logger) : IWorkflowNotifier
{
    public async Task NotifyAsync(WorkflowNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            await inner.NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
            NotifierLogMessages.NotificationSent(logger, notification.Kind, notification.RepositoryId, notification.IssueNumber);
        }
        catch (Exception ex)
        {
            metrics.NotificationFailures.Add(1, new KeyValuePair<string, object?>(LogContextFields.Operation, notification.Kind.ToString()));
            NotifierLogMessages.NotificationFailed(logger, ex.GetType().Name, notification.Kind, notification.RepositoryId, notification.IssueNumber);
            throw;
        }
    }
}
