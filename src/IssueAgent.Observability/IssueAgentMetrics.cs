using System.Diagnostics.Metrics;
using System.Collections.Concurrent;

namespace IssueAgent.Observability;

/// <summary>
/// Metric instruments (specification §30). Labels are bounded to provider/repository/component;
/// issue/PR/session/workflow IDs and error messages are never used as metric labels (they belong
/// in structured logs and trace tags instead).
/// </summary>
public sealed class IssueAgentMetrics : IDisposable
{
    public const string MeterName = "IssueAgent";

    private readonly Meter meter;
    private long lastSuccessfulPollUnixSeconds;
    private readonly ConcurrentDictionary<long, long> activeOperationStarts = new();
    private long nextActiveOperationId;
    private long workspaceBytes;

    public IssueAgentMetrics()
    {
        meter = new Meter(MeterName, "1.0.0");

        PollCount = meter.CreateCounter<long>("issueagent.poll.count", description: "Polling cycles started per repository.");
        PollErrors = meter.CreateCounter<long>("issueagent.poll.errors", description: "Polling cycles that failed per repository.");
        PollDuration = meter.CreateHistogram<double>("issueagent.poll.duration", unit: "s", description: "Polling cycle duration.");
        IssuesDiscovered = meter.CreateCounter<long>("issueagent.issues.discovered", description: "Eligible issues discovered per repository.");
        LastSuccessfulPoll = meter.CreateObservableGauge<long>("issueagent.poll.last_successful_unix_seconds", () => Interlocked.Read(ref lastSuccessfulPollUnixSeconds), description: "Unix timestamp of the most recent successful poll.");
        ActiveOperationAge = meter.CreateObservableGauge(
            "issueagent.operations.oldest_active_age",
            ObserveOldestActiveOperationAge,
            unit: "s",
            description: "Age of the oldest currently active workflow operation.");
        WorkspaceBytes = meter.CreateObservableGauge(
            "issueagent.workspace.bytes",
            () => Interlocked.Read(ref workspaceBytes),
            unit: "By",
            description: "Current workspace storage usage.");

        PlanCount = meter.CreateCounter<long>("issueagent.plans.count", description: "Planning runs started.");
        PlanErrors = meter.CreateCounter<long>("issueagent.plans.errors", description: "Planning runs that failed.");
        PlanDuration = meter.CreateHistogram<double>("issueagent.plans.duration", unit: "s", description: "Planning run duration.");

        ImplementationCount = meter.CreateCounter<long>("issueagent.implementations.count", description: "Implementation runs started.");
        ImplementationErrors = meter.CreateCounter<long>("issueagent.implementations.errors", description: "Implementation runs that failed.");
        ImplementationDuration = meter.CreateHistogram<double>("issueagent.implementations.duration", unit: "s", description: "Implementation run duration.");

        ProviderRequests = meter.CreateCounter<long>("issueagent.provider.requests", description: "Provider API requests.");
        ProviderErrors = meter.CreateCounter<long>("issueagent.provider.errors", description: "Provider API request failures.");
        ProviderDuration = meter.CreateHistogram<double>("issueagent.provider.duration", unit: "s", description: "Provider API request duration.");

        OmpRequests = meter.CreateCounter<long>("issueagent.omp.requests", description: "OMP RPC requests.");
        OmpErrors = meter.CreateCounter<long>("issueagent.omp.errors", description: "OMP RPC request failures.");
        OmpDuration = meter.CreateHistogram<double>("issueagent.omp.duration", unit: "s", description: "OMP RPC request duration.");

        GitOperations = meter.CreateCounter<long>("issueagent.git.operations", description: "Git operations performed.");
        GitErrors = meter.CreateCounter<long>("issueagent.git.errors", description: "Git operations that failed.");
        GitDuration = meter.CreateHistogram<double>("issueagent.git.duration", unit: "s", description: "Git operation duration.");

        LfsOperations = meter.CreateCounter<long>("issueagent.lfs.operations", description: "LFS operations performed.");
        LfsErrors = meter.CreateCounter<long>("issueagent.lfs.errors", description: "LFS operations that failed.");
        LfsDuration = meter.CreateHistogram<double>("issueagent.lfs.duration", unit: "s", description: "LFS operation duration.");

        ActiveOperations = meter.CreateUpDownCounter<long>("issueagent.operations.active", description: "Currently in-flight IssueAgent operations.");
        NotificationFailures = meter.CreateCounter<long>("issueagent.notifications.failures", description: "Notification sink delivery failures after retry.");
    }

    public Counter<long> PollCount { get; }

    public Counter<long> PollErrors { get; }

    public Histogram<double> PollDuration { get; }

    public Counter<long> IssuesDiscovered { get; }
    public ObservableGauge<long> LastSuccessfulPoll { get; }

    public ObservableGauge<double> ActiveOperationAge { get; }

    public ObservableGauge<long> WorkspaceBytes { get; }

    public IDisposable BeginActiveOperation()
    {
        var id = Interlocked.Increment(ref nextActiveOperationId);
        activeOperationStarts[id] = DateTimeOffset.UtcNow.UtcTicks;
        return new ActiveOperation(this, id);
    }

    public void UpdateWorkspaceBytes(long value) =>
        Interlocked.Exchange(ref workspaceBytes, Math.Max(0, value));

    private double ObserveOldestActiveOperationAge()
    {
        var oldest = activeOperationStarts.Values.DefaultIfEmpty(DateTimeOffset.UtcNow.UtcTicks).Min();
        return Math.Max(0, TimeSpan.FromTicks(DateTimeOffset.UtcNow.UtcTicks - oldest).TotalSeconds);
    }

    private sealed class ActiveOperation(IssueAgentMetrics owner, long id) : IDisposable
    {
        public void Dispose() => owner.activeOperationStarts.TryRemove(id, out _);
    }

    public void MarkPollSucceeded() => Interlocked.Exchange(ref lastSuccessfulPollUnixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public Counter<long> PlanCount { get; }

    public Counter<long> PlanErrors { get; }

    public Histogram<double> PlanDuration { get; }

    public Counter<long> ImplementationCount { get; }

    public Counter<long> ImplementationErrors { get; }

    public Histogram<double> ImplementationDuration { get; }

    public Counter<long> ProviderRequests { get; }

    public Counter<long> ProviderErrors { get; }

    public Histogram<double> ProviderDuration { get; }

    public Counter<long> OmpRequests { get; }

    public Counter<long> OmpErrors { get; }

    public Histogram<double> OmpDuration { get; }

    public Counter<long> GitOperations { get; }

    public Counter<long> GitErrors { get; }

    public Histogram<double> GitDuration { get; }

    public Counter<long> LfsOperations { get; }

    public Counter<long> LfsErrors { get; }

    public Histogram<double> LfsDuration { get; }

    public UpDownCounter<long> ActiveOperations { get; }

    public Counter<long> NotificationFailures { get; }

    public void Dispose() => meter.Dispose();
}
