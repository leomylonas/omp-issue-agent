namespace IssueAgent.Workflow.Tests;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

public sealed class RecordingNotifier : IWorkflowNotifier
{
    public List<WorkflowNotification> Notifications { get; } = [];

    public Task NotifyAsync(WorkflowNotification notification, CancellationToken cancellationToken)
    {
        Notifications.Add(notification);
        return Task.CompletedTask;
    }
}
