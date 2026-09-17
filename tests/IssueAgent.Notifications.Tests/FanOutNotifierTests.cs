using IssueAgent.Workflow;

namespace IssueAgent.Notifications.Tests;

public sealed class FanOutNotifierTests
{
    private static readonly WorkflowNotification Notification = new(WorkflowNotificationKind.PlanReady, "github/octo/widgets", 7, "workflow-1", "Plan is ready.");

    [Fact]
    public async Task NotifyAsyncSendsToAllSinksWhenRoutingUnspecified()
    {
        var sinkA = new RecordingSink("a");
        var sinkB = new RecordingSink("b");
        var notifier = new FanOutNotifier([sinkA, sinkB], NoDelayRetryPolicy());

        await notifier.NotifyAsync(Notification, CancellationToken.None);

        Assert.Single(sinkA.Received);
        Assert.Single(sinkB.Received);
    }

    [Fact]
    public async Task NotifyAsyncRoutesToOnlyNamedSinksWhenRoutingConfigured()
    {
        var sinkA = new RecordingSink("a");
        var sinkB = new RecordingSink("b");
        var routing = new Dictionary<WorkflowNotificationKind, IReadOnlySet<string>>
        {
            [WorkflowNotificationKind.PlanReady] = new HashSet<string> { "a" },
        };
        var notifier = new FanOutNotifier([sinkA, sinkB], NoDelayRetryPolicy(), routing);

        await notifier.NotifyAsync(Notification, CancellationToken.None);

        Assert.Single(sinkA.Received);
        Assert.Empty(sinkB.Received);
    }

    [Fact]
    public async Task NotifyAsyncOneSinkFailureDoesNotPreventAnotherSinkFromSucceeding()
    {
        var failingSink = new RecordingSink("failing") { AlwaysThrow = true };
        var healthySink = new RecordingSink("healthy");
        var notifier = new FanOutNotifier([failingSink, healthySink], NoDelayRetryPolicy());

        await notifier.NotifyAsync(Notification, CancellationToken.None);

        Assert.Single(healthySink.Received);
    }

    [Fact]
    public async Task NotifyAsyncReportsSinkFailureAfterExhaustingRetries()
    {
        var failingSink = new RecordingSink("failing") { AlwaysThrow = true };
        string? reportedSinkName = null;
        var notifier = new FanOutNotifier(
            [failingSink],
            NoDelayRetryPolicy() with { MaxAttempts = 2 },
            onSinkFailure: (name, _, _) => reportedSinkName = name);

        await notifier.NotifyAsync(Notification, CancellationToken.None);

        Assert.Equal("failing", reportedSinkName);
        Assert.Equal(2, failingSink.Attempts);
    }

    [Fact]
    public async Task NotifyAsyncRetriesTransientFailuresBeforeSucceeding()
    {
        var flakySink = new RecordingSink("flaky") { FailuresBeforeSuccess = 2 };
        var notifier = new FanOutNotifier([flakySink], NoDelayRetryPolicy());

        await notifier.NotifyAsync(Notification, CancellationToken.None);

        Assert.Equal(3, flakySink.Attempts);
        Assert.Single(flakySink.Received);
    }

    private static RetryPolicy NoDelayRetryPolicy() => RetryPolicy.Default with { InitialDelay = TimeSpan.Zero };

    private sealed class RecordingSink(string name) : INotificationSink
    {
        private int attempts;

        public int Attempts => attempts;

        public string Name { get; } = name;

        public bool AlwaysThrow { get; init; }

        public int FailuresBeforeSuccess { get; init; }

        public List<WorkflowNotification> Received { get; } = [];

        public Task SendAsync(WorkflowNotification notification, CancellationToken cancellationToken)
        {
            attempts++;
            if (AlwaysThrow || attempts <= FailuresBeforeSuccess)
            {
                throw new InvalidOperationException("Simulated sink failure.");
            }

            Received.Add(notification);
            return Task.CompletedTask;
        }
    }
}
